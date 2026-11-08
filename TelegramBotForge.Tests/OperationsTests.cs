using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using TelegramBotForge.AspNetCore;
using TelegramBotForge.Broadcasting;
using TelegramBotForge.Hosting;
using TelegramBotForge.Telemetry;
using TelegramBotForge.Testing;
using TelegramBotForge.Views;

namespace TelegramBotForge.Tests;

public sealed class BroadcastTests
{
    private static BotTestHost CreateWithFailures()
    {
        var bot = TestBot.Create();
        bot.Telegram.Handle<SendMessageRequest>(request => request.ChatId.Identifier switch
        {
            2 => throw new ApiRequestException("Forbidden: bot was blocked by the user", 403),
            3 => throw new ApiRequestException("Bad Request: chat not found", 400),
            4 => throw new ApiRequestException("Internal Server Error", 500),
            _ => null
        });
        return bot;
    }

    [Fact]
    public async Task Sends_to_everyone_and_sorts_out_failures()
    {
        await using var bot = CreateWithFailures();
        var broadcaster = bot.Services.GetRequiredService<IBotBroadcaster>();
        var unreachable = new ConcurrentBag<long>();
        var delivered = new ConcurrentDictionary<long, int>();
        BroadcastProgress? last = null;

        var report = await broadcaster.SendAsync([1, 2, 3, 4, 5, 1], new BotMessage("News!"), new BroadcastOptions
        {
            MessagesPerSecond = 100,
            OnUnreachable = (chat, _) => { unreachable.Add(chat); return ValueTask.CompletedTask; },
            OnSent = (chat, messageId, _) => { delivered[chat] = messageId; return ValueTask.CompletedTask; },
            Progress = new SyncProgress(progress => last = progress)
        });

        Assert.Equal(5, report.Total);
        Assert.Equal(2, report.Sent);
        Assert.Equal([2, 3], report.Unreachable.Order());
        Assert.Equal([4], report.Failed.Keys);
        Assert.Equal([4], report.Remaining);
        Assert.False(report.Cancelled);
        Assert.Equal([2, 3], unreachable.Order());
        Assert.Equal([1, 5], delivered.Keys.Order());
        Assert.All(delivered.Values, id => Assert.True(id > 0));
        Assert.Equal(5, last!.Done);
        Assert.Equal("News!", bot.Telegram.MessagesIn(5).Single().Text);
    }

    [Fact]
    public async Task Per_chat_messages_can_be_skipped()
    {
        await using var bot = TestBot.Create();
        var report = await bot.Services.GetRequiredService<IBotBroadcaster>().SendAsync([10, 11, 12],
            (chat, _) => ValueTask.FromResult(chat == 11 ? null : new BotMessage($"Hi {chat}")));

        Assert.Equal(2, report.Sent);
        Assert.Equal(1, report.Skipped);
        Assert.Equal("Hi 12", bot.Telegram.MessagesIn(12).Single().Text);
    }

    [Fact]
    public async Task Cancelled_broadcast_returns_what_is_left()
    {
        await using var bot = TestBot.Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var report = await bot.Services.GetRequiredService<IBotBroadcaster>().SendAsync([1, 2, 3], new BotMessage("x"), ct: cts.Token);

        Assert.True(report.Cancelled);
        Assert.Equal(0, report.Sent);
        Assert.Equal([1, 2, 3], report.Remaining.Order());
    }

    [Fact]
    public async Task Keeps_to_its_speed()
    {
        await using var bot = TestBot.Create();
        var watch = Stopwatch.StartNew();
        var report = await bot.Services.GetRequiredService<IBotBroadcaster>().SendAsync(
            Enumerable.Range(1, 15).Select(i => (long)i), new BotMessage("x"), new BroadcastOptions { MessagesPerSecond = 10 });

        Assert.Equal(15, report.Sent);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(400), watch.Elapsed.ToString());
    }

    private sealed class SyncProgress(Action<BroadcastProgress> report) : IProgress<BroadcastProgress>
    {
        public void Report(BroadcastProgress value) => report(value);
    }
}

public sealed class TelemetryTests
{
    [Fact]
    public async Task Updates_are_counted_and_traced()
    {
        var measurements = new ConcurrentBag<(string Name, long Value, string? Outcome, string? Type)>();
        using var meters = new MeterListener();
        meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == BotTelemetry.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var all = tags.ToArray();
            string? Tag(string key) => all.FirstOrDefault(tag => tag.Key == key).Value?.ToString();
            measurements.Add((instrument.Name, value, Tag("outcome"), Tag("update.type")));
        });
        meters.Start();

        var activities = new ConcurrentBag<Activity>();
        using var tracing = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BotTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(tracing);

        await using var bot = TestBot.Create();
        var user = bot.PrivateChat(userId: 4242);
        await user.SendText("/start");

        Assert.Contains(measurements, m => m is { Name: "telegrambot.updates", Outcome: "handled", Type: "Message" });
        Assert.Contains(activities, a => a.OperationName == "telegram.update" &&
                                         (string?)a.GetTagItem("telegram.action") == "StartController.Start");
    }
}

public sealed class ConversationLockTests
{
    [Fact]
    public async Task Every_update_runs_inside_the_lock_of_its_conversation()
    {
        await using var bot = TestBot.Create(bot: b => b.UseConversationLock<RecordingLock>());
        var queues = bot.Services.GetRequiredService<UpdateQueues>();
        await queues.StartAsync(CancellationToken.None);

        var chat = new Chat { Id = 777, Type = Telegram.Bot.Types.Enums.ChatType.Private };
        var user = new User { Id = 777, FirstName = "L" };
        for (var i = 1; i <= 5; i++)
        {
            await queues.EnqueueAsync(new Update
            {
                Id = i,
                Message = new Message { Id = i, Date = DateTime.UtcNow, Chat = chat, From = user, Text = $"/echo {i}" }
            }, CancellationToken.None);
        }

        await queues.StopAsync(CancellationToken.None);

        var recorder = (RecordingLock)bot.Services.GetRequiredService<IConversationLock>();
        Assert.Equal(Enumerable.Repeat("chat:777", 5), recorder.Keys);
        Assert.Equal(0, recorder.Held);
    }

    public sealed class RecordingLock : IConversationLock
    {
        private int _held;

        public ConcurrentQueue<string> Keys { get; } = new();

        public int Held => _held;

        public ValueTask<IAsyncDisposable> AcquireAsync(string key, CancellationToken ct)
        {
            Keys.Enqueue(key);
            if (Interlocked.Increment(ref _held) != 1)
                throw new InvalidOperationException("Two updates of one conversation held the lock at once.");
            return ValueTask.FromResult<IAsyncDisposable>(new Release(this));
        }

        private sealed class Release(RecordingLock owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Interlocked.Decrement(ref owner._held);
                return ValueTask.CompletedTask;
            }
        }
    }
}

public sealed class HealthCheckTests
{
    private static TelegramBotHealthCheck CheckFor(BotTestHost bot) => new(
        bot.Telegram,
        bot.Services.GetRequiredService<BotIdentity>(),
        bot.Services.GetRequiredService<TelegramBotBuilder>(),
        TimeProvider.System);

    private static Task<HealthCheckResult> Run(TelegramBotHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("telegram", check, null, null)
        });

    [Fact]
    public async Task Healthy_when_telegram_answers()
    {
        await using var bot = TestBot.Create();
        Assert.Equal(HealthStatus.Healthy, (await Run(CheckFor(bot))).Status);
    }

    [Fact]
    public async Task Unhealthy_when_telegram_cannot_be_reached()
    {
        await using var bot = TestBot.Create();
        bot.Telegram.Handle<GetMeRequest>(_ => throw new HttpRequestException("no route to host"));
        var result = await Run(CheckFor(bot));
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("no route to host", result.Description);
    }

    [Fact]
    public async Task Unhealthy_when_the_token_was_rejected()
    {
        await using var bot = TestBot.Create();
        bot.Services.GetRequiredService<BotIdentity>().TokenRejected = true;
        Assert.Equal(HealthStatus.Unhealthy, (await Run(CheckFor(bot))).Status);
    }

    [Fact]
    public async Task Degraded_when_another_process_polls()
    {
        await using var bot = TestBot.Create();
        bot.Services.GetRequiredService<BotIdentity>().LastConflict = DateTimeOffset.UtcNow;
        Assert.Equal(HealthStatus.Degraded, (await Run(CheckFor(bot))).Status);
    }

    [Fact]
    public async Task Webhook_delivery_errors_degrade()
    {
        await using var bot = TestBot.Create(
            bot: b => b.UseWebhook(),
            configuration: new Dictionary<string, string?> { ["Webhook:Url"] = "https://bot.example.com/telegram/webhook" });
        bot.Telegram.Handle<GetWebhookInfoRequest>(_ => new WebhookInfo
        {
            Url = "https://bot.example.com/telegram/webhook",
            LastErrorDate = DateTime.UtcNow.AddMinutes(-1),
            LastErrorMessage = "Connection timed out"
        });

        var result = await Run(CheckFor(bot));
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Connection timed out", result.Description);
    }
}
