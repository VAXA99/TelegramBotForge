using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using TelegramBotForge.Broadcasting;
using TelegramBotForge.Hosting;
using TelegramBotForge.Middleware;
using TelegramBotForge.Results;
using TelegramBotForge.State;
using Message = TelegramBotForge.Views.Message;

namespace TelegramBotForge.Tests;

public sealed class OuterDeliveryFinallyMiddleware : IBotMiddleware
{
    public async Task<IBotResult?> InvokeAsync(BotContext context, BotNext next, CancellationToken ct)
    {
        var result = await next(context, ct);
        return result?.Finally(() => context.State!.Set("outer_finished", true));
    }
}

public sealed class OuterDeliveryClearMiddleware : IBotMiddleware
{
    public async Task<IBotResult?> InvokeAsync(BotContext context, BotNext next, CancellationToken ct)
    {
        var result = await next(context, ct);
        return result?.Finally(() => context.State!.Clear());
    }
}

public sealed class DeliveryRegressionTests
{
    private static Message Screen() => Message.Create<FactoryView>(nameof(FactoryView.Home)).Paragraph("Delivery regression");

    [Theory]
    [InlineData("delivered")]
    [InlineData("finally")]
    [InlineData("both")]
    [InlineData("cancel_delivered")]
    [InlineData("cancel_finally")]
    public async Task Sender_preserves_receipt_and_original_post_delivery_errors(string outcome)
    {
        await using var bot = TestBot.Create();
        using var cancellation = new CancellationTokenSource();
        Exception deliveredError = outcome == "cancel_delivered"
            ? new OperationCanceledException(cancellation.Token) : new InvalidOperationException("delivered callback failed");
        Exception finallyError = outcome == "cancel_finally"
            ? new OperationCanceledException(cancellation.Token) : new InvalidOperationException("finally callback failed");
        MessageDelivery? observed = null;
        var message = Screen().OnDelivered((receipt, _) =>
        {
            observed = receipt;
            if (outcome == "cancel_delivered")
                cancellation.Cancel();
            if (outcome is "delivered" or "both" or "cancel_delivered")
                throw deliveredError;
            return Task.CompletedTask;
        }).Finally(token =>
        {
            Assert.False(token.IsCancellationRequested);
            if (outcome == "cancel_finally")
                cancellation.Cancel();
            if (outcome is "finally" or "both" or "cancel_finally")
                throw finallyError;
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<MessageDeliveryException>(() =>
            bot.Services.GetRequiredService<IBotMessageSender>().SendAsync(42, message, cancellation.Token));

        Assert.Same(observed, error.Delivery);
        Assert.Equal(Assert.Single(bot.Telegram.MessagesIn(42)).Id, error.Delivery.PrimaryMessageId);
        var errors = error.Flatten().InnerExceptions;
        if (outcome is "delivered" or "both" or "cancel_delivered")
            Assert.Contains(deliveredError, errors);
        if (outcome is "finally" or "both" or "cancel_finally")
            Assert.Contains(finallyError, errors);
        Assert.Equal(outcome == "both" ? 2 : 1, errors.Count);
        Assert.Single(bot.Telegram.RequestsOf<SendMessageRequest>());
    }

    [Fact]
    public async Task Cleanup_failure_after_edit_preserves_the_updated_receipt()
    {
        await using var bot = TestBot.Create();
        var sender = bot.Services.GetRequiredService<IBotMessageSender>();
        var previous = await sender.SendAsync(42, Screen().Photo(Media.MediaFile.FromFileId(Media.MediaType.Photo, "photo")));
        var cleanupError = new ApiRequestException("Forbidden", 403);
        bot.Telegram.Handle<DeleteMessageRequest>(_ => throw cleanupError);

        var error = await Assert.ThrowsAsync<MessageDeliveryException>(() => sender.EditAsync(previous, Screen().Paragraph("Updated")));

        Assert.Equal(previous.PrimaryMessageId, error.Delivery.PrimaryMessageId);
        Assert.Empty(error.Delivery.AssociatedMessageIds);
        Assert.Contains(cleanupError, error.Flatten().InnerExceptions);
        Assert.Contains("Updated", bot.Telegram.MessagesIn(42).Last().Text);
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("finally")]
    public async Task Remaining_retries_only_real_send_failures(string outcome)
    {
        await using var bot = TestBot.Create();
        var apiError = new ApiRequestException("Internal Server Error", 500);
        var callbackError = new InvalidOperationException("post-delivery failed");
        bot.Telegram.Handle<SendMessageRequest>(request => request.ChatId.Identifier == 3 ? throw apiError : null);
        var broadcaster = bot.Services.GetRequiredService<IBotBroadcaster>();
        var notified = new System.Collections.Concurrent.ConcurrentDictionary<long, int>();
        BroadcastProgress? progress = null;
        var report = await broadcaster.SendAsync([1, 2, 3], (chat, _) =>
        {
            var message = Screen();
            if (chat == 1)
                message = outcome == "delivered"
                    ? message.OnDelivered((_, _) => throw callbackError)
                    : message.Finally((Action)(() => throw callbackError));
            return ValueTask.FromResult<Message?>(message);
        }, new BroadcastOptions
        {
            OnSent = (chat, messageId, _) => { notified[chat] = messageId; return ValueTask.CompletedTask; },
            Progress = new SyncProgress(value => progress = value)
        });

        Assert.Equal(2, report.Sent);
        Assert.Equal(new long[] { 3 }, report.Failed.Keys);
        Assert.Equal(new long[] { 3 }, report.Remaining);
        Assert.Empty(report.Unreachable);
        Assert.Equal(new long[] { 1 }, report.PostDeliveryErrors.Keys);
        Assert.Contains(callbackError, report.PostDeliveryErrors[1].Flatten().InnerExceptions);
        Assert.Equal(new long[] { 1, 2 }, notified.Keys.Order());
        Assert.Equal(3, progress!.Done);
        Assert.Equal(2, progress.Sent);
        Assert.Equal(1, progress.Failed);
        bot.Telegram.Handle<SendMessageRequest>(_ => null);

        var retry = await broadcaster.SendAsync(report.Remaining, Screen());

        Assert.Equal(1, retry.Sent);
        Assert.Empty(retry.Remaining);
        Assert.Single(bot.Telegram.MessagesIn(1));
        Assert.Single(bot.Telegram.MessagesIn(2));
        Assert.Single(bot.Telegram.MessagesIn(3));
        Assert.Equal(new[] { 1, 1, 2 }, new long[] { 1, 2, 3 }.Select(chat =>
            bot.Telegram.RequestsOf<SendMessageRequest>().Count(request => request.ChatId.Identifier == chat)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OnSent_errors_do_not_double_count_delivered_chats(bool legacy, bool cancel)
    {
        await using var bot = TestBot.Create();
        using var cancellation = new CancellationTokenSource();
        Exception callbackError = cancel ? new OperationCanceledException(cancellation.Token) : new ApiRequestException("Forbidden", 403);
        var options = new BroadcastOptions
        {
            OnSent = (_, _, _) =>
            {
                if (cancel)
                    cancellation.Cancel();
                throw callbackError;
            }
        };
        var broadcaster = bot.Services.GetRequiredService<IBotBroadcaster>();
        var report = legacy
            ? await broadcaster.SendAsync([42], new Views.BotMessage("legacy"), options, cancellation.Token)
            : await broadcaster.SendAsync([42], Screen(), options, cancellation.Token);

        Assert.Equal(1, report.Sent);
        Assert.Empty(report.Failed);
        Assert.Empty(report.Unreachable);
        Assert.Empty(report.Remaining);
        Assert.Equal(cancel, report.Cancelled);
        var error = Assert.Single(report.PostDeliveryErrors).Value;
        Assert.Same(callbackError, Assert.Single(error.InnerExceptions));
        Assert.Equal(Assert.Single(bot.Telegram.MessagesIn(42)).Id, error.Delivery.PrimaryMessageId);
    }

    [Fact]
    public async Task OnSent_runs_after_OnDelivered_failure_and_both_errors_are_preserved()
    {
        await using var bot = TestBot.Create();
        var deliveredError = new InvalidOperationException("delivered failed");
        var sentError = new InvalidOperationException("sent failed");
        var report = await bot.Services.GetRequiredService<IBotBroadcaster>().SendAsync([42],
            Screen().OnDelivered((_, _) => throw deliveredError),
            new BroadcastOptions { OnSent = (_, _, _) => throw sentError });

        Assert.Equal(1, report.Sent);
        Assert.Empty(report.Remaining);
        Assert.Empty(report.Failed);
        var errors = report.PostDeliveryErrors[42].Flatten().InnerExceptions;
        Assert.Equal(2, errors.Count);
        Assert.Contains(deliveredError, errors);
        Assert.Contains(sentError, errors);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cancellation_after_confirmation_keeps_chat_sent(bool finalize, bool throwCancellation)
    {
        await using var bot = TestBot.Create();
        using var cancellation = new CancellationTokenSource();
        var cancelled = new OperationCanceledException(cancellation.Token);
        Task Cancel()
        {
            cancellation.Cancel();
            if (throwCancellation)
                throw cancelled;
            return Task.CompletedTask;
        }
        var message = finalize ? Screen().Finally(Cancel) : Screen().OnDelivered((_, _) => Cancel());

        var report = await bot.Services.GetRequiredService<IBotBroadcaster>().SendAsync([42], message, ct: cancellation.Token);

        Assert.Equal(1, report.Sent);
        Assert.True(report.Cancelled);
        Assert.Empty(report.Remaining);
        Assert.Empty(report.Failed);
        Assert.Empty(report.Unreachable);
        Assert.Equal(throwCancellation ? 1 : 0, report.PostDeliveryErrors.Count);
        if (throwCancellation)
            Assert.Contains(cancelled, report.PostDeliveryErrors[42].Flatten().InnerExceptions);
        Assert.Single(bot.Telegram.MessagesIn(42));
    }

    [Fact]
    public async Task Api_failure_before_delivery_remains_the_original_exception()
    {
        await using var bot = TestBot.Create();
        var apiError = new ApiRequestException("Internal Server Error", 500);
        bot.Telegram.Handle<SendMessageRequest>(_ => throw apiError);
        var error = await Assert.ThrowsAsync<ApiRequestException>(() => bot.Services.GetRequiredService<IBotMessageSender>().SendAsync(42, Screen()));
        Assert.Same(apiError, error);
        Assert.Empty(bot.Telegram.MessagesIn(42));
        var report = await bot.Services.GetRequiredService<IBotBroadcaster>().SendAsync([42], Screen());
        Assert.Equal(0, report.Sent);
        Assert.Equal(new long[] { 42 }, report.Failed.Keys);
        Assert.Equal(new long[] { 42 }, report.Remaining);
        Assert.Empty(report.PostDeliveryErrors);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("api_error")]
    [InlineData("callback_error")]
    [InlineData("cancel")]
    public async Task State_is_saved_after_outer_middleware_finalizers(string outcome)
    {
        var storage = new SnapshotStorage();
        await using var bot = TestBot.Create(services: services => services.AddSingleton<IStateStorage>(storage),
            bot: builder => builder.UseMiddleware<OuterDeliveryFinallyMiddleware>());
        using var cancellation = new CancellationTokenSource();
        bot.Telegram.Handle<SendMessageRequest>(_ =>
        {
            Assert.True(Assert.Single(storage.SavedStates).Get<bool>("action"));
            Assert.False(storage.SavedStates[0].Data.ContainsKey("outer_finished"));
            if (outcome == "api_error")
                throw new ApiRequestException("Internal Server Error", 500);
            if (outcome == "cancel")
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return null;
        });
        var user = bot.PrivateChat();

        await user.SendText("/delivery_state " + outcome, cancellation.Token);

        var state = await user.GetState();
        Assert.True(state!.Get<bool>("outer_finished"));
        Assert.True(state.Get<bool>("finished"));
        Assert.Equal(2, storage.SavedStates.Count);
        Assert.False(storage.SavedTokens[1].CanBeCanceled);
        Assert.False(storage.SavedStates[0].Data.ContainsKey("outer_finished"));
        state.Clear();
        Assert.True((await user.GetState())!.Get<bool>("outer_finished"));
    }

    [Fact]
    public async Task Outer_finalizer_clearing_state_removes_the_persisted_snapshot()
    {
        var storage = new SnapshotStorage();
        await using var bot = TestBot.Create(services: services => services.AddSingleton<IStateStorage>(storage),
            bot: builder => builder.UseMiddleware<OuterDeliveryClearMiddleware>());
        var user = bot.PrivateChat();
        await user.SendText("/delivery_state");
        Assert.Null(await user.GetState());
        Assert.Single(storage.SavedStates);
        Assert.Equal(1, storage.Removes);
    }

    [Fact]
    public async Task Unchanged_state_is_not_saved_again()
    {
        var storage = new SnapshotStorage();
        await using var bot = TestBot.Create(services: services => services.AddSingleton<IStateStorage>(storage));
        var user = bot.PrivateChat();
        await user.SendText("/signup");
        Assert.Single(storage.SavedStates);
        Assert.NotNull(await user.GetState());
    }

    [Fact]
    public async Task Action_failure_does_not_persist_its_state_changes()
    {
        var storage = new SnapshotStorage();
        await using var bot = TestBot.Create(services: services => services.AddSingleton<IStateStorage>(storage),
            bot: builder => builder.UseMiddleware<OuterDeliveryFinallyMiddleware>());
        var user = bot.PrivateChat();
        await user.SendText("/delivery_action_failure");
        Assert.Null(await user.GetState());
        Assert.Empty(storage.SavedStates);
        Assert.Equal(0, storage.Removes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatcher_preserves_execution_and_final_storage_errors(bool delivered)
    {
        var storageError = new InvalidOperationException("final save failed");
        var storage = new SnapshotStorage { FinalSaveError = storageError };
        var logger = new ExceptionLogger();
        await using var bot = TestBot.Create(services: services =>
        {
            services.AddSingleton<IStateStorage>(storage);
            services.AddSingleton<ILogger<UpdateDispatcher>>(logger);
        }, bot: builder => builder.UseMiddleware<OuterDeliveryFinallyMiddleware>());
        var apiError = new ApiRequestException("send failed", 500);
        if (!delivered)
            bot.Telegram.Handle<SendMessageRequest>(_ => throw apiError);
        await bot.PrivateChat().SendText("/delivery_state callback_error");
        var error = Assert.IsAssignableFrom<AggregateException>(Assert.Single(logger.Errors));
        var errors = error.Flatten().InnerExceptions;
        Assert.Contains(storageError, errors);
        if (delivered)
        {
            Assert.IsType<MessageDeliveryException>(error);
            Assert.Contains(errors, exception => exception.Message == "callback failed");
        }
        else
            Assert.Contains(apiError, errors);
        Assert.Equal(2, storage.SavedTokens.Count);
        Assert.False(storage.SavedTokens[1].CanBeCanceled);
    }

    private sealed class SnapshotStorage : IStateStorage
    {
        private readonly Dictionary<StateKey, ChatState> _states = [];
        public List<ChatState> SavedStates { get; } = [];
        public List<CancellationToken> SavedTokens { get; } = [];
        public int Removes { get; private set; }
        public Exception? FinalSaveError { get; init; }

        public ValueTask<ChatState?> GetAsync(StateKey key, CancellationToken ct) =>
            ValueTask.FromResult<ChatState?>(_states.TryGetValue(key, out var state) ? state.Clone() : null);

        public ValueTask SaveAsync(StateKey key, ChatState state, TimeSpan timeToLive, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SavedTokens.Add(ct);
            if (SavedStates.Count != 0 && FinalSaveError is { } error)
                throw error;
            _states[key] = state.Clone();
            SavedStates.Add(state.Clone());
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(StateKey key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _states.Remove(key);
            Removes++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SyncProgress(Action<BroadcastProgress> callback) : IProgress<BroadcastProgress>
    {
        public void Report(BroadcastProgress value) => callback(value);
    }

    private sealed class ExceptionLogger : ILogger<UpdateDispatcher>
    {
        public List<Exception> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
                Errors.Add(exception);
        }
    }
}
