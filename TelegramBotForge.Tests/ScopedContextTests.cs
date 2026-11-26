using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBotForge.Controllers;
using TelegramBotForge.Hosting;
using TelegramBotForge.Input;
using TelegramBotForge.Middleware;
using TelegramBotForge.Results;
using Message = TelegramBotForge.Views.Message;

namespace TelegramBotForge.Tests;

public sealed class ContextProbe
{
    public ConcurrentBag<(string Stage, BotContext Context)> Entries { get; } = [];
    public bool Parallel { get; set; }
    private int _arrivals;
    private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task Meet(CancellationToken ct)
    {
        if (!Parallel)
            return;
        if (Interlocked.Increment(ref _arrivals) == 2)
            _both.TrySetResult();
        await _both.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }
}

public sealed class ContextSession(BotContext context)
{
    public BotContext Context { get; } = context;
    public void SaveId(int id) => Context.State!.Set("scoped.message", id);
}

public sealed class ContextProbeMiddleware : IBotMiddleware
{
    private readonly BotContext _context;
    private readonly ContextProbe _probe;
    public ContextProbeMiddleware(BotContext context, ContextSession session, ContextProbe probe)
    {
        Assert.Same(context, session.Context);
        _context = context;
        _probe = probe;
        probe.Entries.Add(("constructor", context));
    }
    public async Task<IBotResult?> InvokeAsync(BotContext context, BotNext next, CancellationToken ct)
    {
        Assert.Same(_context, context);
        _probe.Entries.Add(("middleware", context));
        var result = await next(context, ct);
        return result?.Finally(() =>
        {
            _probe.Entries.Add(("finally", context));
            context.State!.Set("scoped.finished", true);
        });
    }
}

public sealed class ContextProbeController(ContextSession session, ContextProbe probe) : BotController
{
    [Command("context_probe")]
    public async Task<IBotResult> Run(CancellationToken ct)
    {
        Assert.Same(Context, session.Context);
        probe.Entries.Add(("action", Context));
        await probe.Meet(ct);
        return Reply("Context").OnDelivered((receipt, _) =>
        {
            Assert.Same(Context, Context.Services.GetRequiredService<BotContext>());
            probe.Entries.Add(("delivered", session.Context));
            session.SaveId(receipt.PrimaryMessageId);
            return Task.CompletedTask;
        });
    }
}

public sealed class ScopedContextTests
{
    [Fact]
    public async Task Same_context_is_injected_before_middleware_and_survives_delivery_finalizers()
    {
        var probe = new ContextProbe();
        await using var bot = TestBot.Create(services: services =>
        {
            services.AddSingleton(probe);
            services.AddScoped<ContextSession>();
        }, bot: builder => builder.UseMiddleware<ContextProbeMiddleware>());
        var user = bot.PrivateChat();
        await user.SendText("/context_probe");
        var expected = probe.Entries.First().Context;
        Assert.Equal(5, probe.Entries.Count);
        Assert.All(probe.Entries, entry => Assert.Same(expected, entry.Context));
        var state = await user.GetState();
        Assert.True(state!.Get<bool>("scoped.finished"));
        Assert.True(state.Get<int>("scoped.message") > 0);
        await using var outside = bot.Services.CreateAsyncScope();
        var error = Assert.Throws<InvalidOperationException>(() => outside.ServiceProvider.GetRequiredService<BotContext>());
        Assert.Contains("only inside", error.Message);
    }

    [Fact]
    public async Task Parallel_updates_have_separate_contexts_and_new_scopes_have_no_current_context()
    {
        var probe = new ContextProbe { Parallel = true };
        await using var bot = TestBot.Create(services: services =>
        {
            services.AddSingleton(probe);
            services.AddScoped<ContextSession>();
        }, bot: builder => builder.UseMiddleware<ContextProbeMiddleware>());
        var dispatcher = bot.Services.GetRequiredService<UpdateDispatcher>();
        Update Incoming(long id) => new()
        {
            Message = new Telegram.Bot.Types.Message
            {
                Chat = new Chat { Id = id, Type = ChatType.Private },
                From = new User { Id = id, FirstName = "Parallel" },
                Text = "/context_probe"
            }
        };
        await Task.WhenAll(dispatcher.DispatchAsync(Incoming(101), false, CancellationToken.None), dispatcher.DispatchAsync(Incoming(102), false, CancellationToken.None));
        var groups = probe.Entries.GroupBy(entry => entry.Context.UserId).ToArray();
        Assert.Equal(2, groups.Length);
        Assert.NotSame(groups[0].First().Context, groups[1].First().Context);
        foreach (var group in groups)
            Assert.All(group, entry => Assert.Same(group.First().Context, entry.Context));
        await using var outside = bot.Services.CreateAsyncScope();
        Assert.Throws<InvalidOperationException>(() => outside.ServiceProvider.GetRequiredService<ContextSession>());
    }

    [Fact]
    public async Task Background_delivery_injects_its_own_context_without_a_user_or_state()
    {
        var probe = new ContextProbe();
        await using var bot = TestBot.Create(services: services =>
        {
            services.AddSingleton(probe);
            services.AddScoped<ContextSession>();
            services.AddScoped<IBotResultExecutor, BackgroundExecutor>();
        });
        var message = Message.Create<FactoryView>(nameof(FactoryView.Home)).Paragraph("Background");
        await Task.WhenAll(bot.Services.GetRequiredService<IBotMessageSender>().SendAsync(201, message),
            bot.Services.GetRequiredService<IBotMessageSender>().SendAsync(202, message));
        Assert.Equal(2, probe.Entries.Count);
        Assert.Equal(2, probe.Entries.Select(entry => entry.Context).Distinct().Count());
        Assert.All(probe.Entries, entry =>
        {
            Assert.Null(entry.Context.From);
            Assert.Null(entry.Context.UserId);
            Assert.Null(entry.Context.State);
        });
        await using var outside = bot.Services.CreateAsyncScope();
        Assert.Throws<InvalidOperationException>(() => outside.ServiceProvider.GetRequiredService<BotContext>());
    }

    private sealed class BackgroundExecutor(BotContext current, ContextSession session, ContextProbe probe) : IBotResultExecutor
    {
        public Task ExecuteAsync(BotContext context, IBotResult result, CancellationToken ct)
        {
            Assert.Same(current, context);
            Assert.Same(current, session.Context);
            Assert.Throws<InvalidOperationException>(() => context.Services.GetRequiredService<ITemporaryInput>().Begin(
                "input", TemporaryInputKind.Location, _ => Message.Create<FactoryView>(nameof(FactoryView.Home)),
                new TemporaryInputOptions(TimeSpan.FromMinutes(1), "Expired")));
            probe.Entries.Add(("background", current));
            return new TelegramResultExecutor().ExecuteAsync(context, result, ct);
        }
    }
}
