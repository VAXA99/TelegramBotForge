using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using TelegramBotForge.Controllers;
using TelegramBotForge.Middleware;
using TelegramBotForge.Payments;
using TelegramBotForge.Results;
using TelegramBotForge.State;
using Message = TelegramBotForge.Views.Message;

namespace TelegramBotForge.Tests;

public sealed class DeliveryCultureProbe
{
    public ConcurrentQueue<(string Stage, string Culture, string Ui)> Entries { get; } = new();
    public void Record(string stage) => Entries.Enqueue((stage, CultureInfo.CurrentCulture.Name, CultureInfo.CurrentUICulture.Name));
}

public sealed class DeliveryCultureController(DeliveryCultureProbe probe) : BotController
{
    [Command("culture_invoice")]
    public IBotResult InvoiceScreen() => Observe(Invoice("culture_product"));

    [Command("culture_structured")]
    public IBotResult Structured() => Observe(Reply(Message.Create<FactoryView>(nameof(FactoryView.Home)).Paragraph("Culture")
        .OnDelivered((_, _) => { probe.Record("message_delivered"); return Task.CompletedTask; })
        .Finally(() => probe.Record("message_finally"))));

    private IBotResult Observe(IBotResult result)
    {
        probe.Record("action");
        State.Set("action", true);
        return result.OnDelivered((receipt, _) =>
        {
            probe.Record("delivered");
            State.Set("receipt", receipt.PrimaryMessageId);
            return Task.CompletedTask;
        }).Finally(() =>
        {
            probe.Record("finally");
            State.Set("finished", true);
        });
    }
}

public sealed class DeliveryCultureFinallyMiddleware(DeliveryCultureProbe probe) : IBotMiddleware
{
    public async Task<IBotResult?> InvokeAsync(BotContext context, BotNext next, CancellationToken ct)
    {
        var result = await next(context, ct);
        return result?.Finally(() => probe.Record("outer_finally"));
    }
}

public sealed class DeliveryCultureProducts(DeliveryCultureProbe probe) : IStarsProducts
{
    public async ValueTask<StarsProduct?> FindAsync(string code, CancellationToken ct)
    {
        await Task.Yield();
        probe.Record("products");
        return new StarsProduct(code, "Culture product", CultureInfo.CurrentUICulture.Name, 1);
    }
}

public sealed class DeliveryCultureResource : IAsyncDisposable
{
    private readonly DeliveryCultureProbe _probe;
    public DeliveryCultureResource(DeliveryCultureProbe probe)
    {
        _probe = probe;
        probe.Record("result_service");
    }
    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        _probe.Record("scope_disposed");
    }
}

public sealed class DeliveryLocalizationTests
{
    [Theory]
    [InlineData("invoice", false, true)]
    [InlineData("invoice", true, true)]
    [InlineData("invoice", false, false)]
    [InlineData("invoice", true, false)]
    [InlineData("structured", false, true)]
    [InlineData("structured", true, true)]
    [InlineData("structured", false, false)]
    [InlineData("structured", true, false)]
    public async Task Selected_culture_covers_delivery_callbacks_finalization_and_scope_cleanup(string screen, bool fail, bool setCurrentCulture)
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            var probe = new DeliveryCultureProbe();
            await using var bot = TestBot.Create(services: services =>
            {
                services.AddSingleton(probe);
                services.AddSingleton<IStarsProducts, DeliveryCultureProducts>();
                services.AddSingleton<IStateStorage>(new CultureStateStorage(probe));
                services.AddScoped<DeliveryCultureResource>();
                services.AddScoped<IBotResultExecutor, CultureExecutor>();
            }, bot: builder => builder.UseLocalization(options =>
            {
                options.DefaultCulture = "en-US";
                options.SupportedCultures = ["en-US", "ru"];
                options.SetCurrentCulture = setCurrentCulture;
            }).UseMiddleware<DeliveryCultureFinallyMiddleware>());

            object? Sending()
            {
                probe.Record("send");
                if (fail)
                    throw new ApiRequestException("send failed", 500);
                return null;
            }
            bot.Telegram.Handle<SendInvoiceRequest>(_ => Sending());
            bot.Telegram.Handle<SendMessageRequest>(_ => Sending());

            await bot.PrivateChat(languageCode: "ru").SendText("/culture_" + screen);

            var entries = probe.Entries.ToArray();
            Assert.Contains(entries, entry => entry.Stage == "action");
            Assert.Contains(entries, entry => entry.Stage == "send");
            Assert.Contains(entries, entry => entry.Stage == "finally");
            Assert.Contains(entries, entry => entry.Stage == "outer_finally");
            Assert.Contains(entries, entry => entry.Stage == "result_service");
            Assert.Equal("scope_disposed", entries.Last().Stage);
            Assert.Equal(2, entries.Count(entry => entry.Stage == "state_save"));
            Assert.Equal(fail ? 0 : 1, entries.Count(entry => entry.Stage == "delivered"));
            if (screen == "invoice")
            {
                Assert.Contains(entries, entry => entry.Stage == "products");
                Assert.Equal("ru", Assert.Single(bot.Telegram.RequestsOf<SendInvoiceRequest>()).Description);
            }
            else
            {
                Assert.Contains(entries, entry => entry.Stage == "message_finally");
                Assert.Equal(fail ? 0 : 1, entries.Count(entry => entry.Stage == "message_delivered"));
            }
            Assert.All(entries, entry =>
            {
                Assert.Equal("ru", entry.Ui);
                Assert.Equal(setCurrentCulture ? "ru" : "de-DE", entry.Culture);
            });
            Assert.Equal("de-DE", CultureInfo.CurrentCulture.Name);
            Assert.Equal("en-US", CultureInfo.CurrentUICulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    private sealed class CultureExecutor(DeliveryCultureResource resource) : IBotResultExecutor
    {
        public Task ExecuteAsync(BotContext context, IBotResult result, CancellationToken ct)
        {
            GC.KeepAlive(resource);
            return new TelegramResultExecutor().ExecuteAsync(context, result, ct);
        }
    }

    private sealed class CultureStateStorage(DeliveryCultureProbe probe) : IStateStorage
    {
        private readonly InMemoryStateStorage _storage = new();
        public ValueTask<ChatState?> GetAsync(StateKey key, CancellationToken ct) => _storage.GetAsync(key, ct);
        public async ValueTask SaveAsync(StateKey key, ChatState state, TimeSpan timeToLive, CancellationToken ct)
        {
            await Task.Yield();
            probe.Record("state_save");
            await _storage.SaveAsync(key, state, timeToLive, ct);
        }
        public ValueTask RemoveAsync(StateKey key, CancellationToken ct) => _storage.RemoveAsync(key, ct);
    }
}
