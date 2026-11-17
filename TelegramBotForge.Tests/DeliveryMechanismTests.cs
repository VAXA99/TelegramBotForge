using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using TelegramBotForge.Broadcasting;
using TelegramBotForge.Controllers;
using TelegramBotForge.Media;
using TelegramBotForge.Results;
using TelegramBotForge.State;
using Message = TelegramBotForge.Views.Message;

namespace TelegramBotForge.Tests;

public sealed class DeliveryStateController : BotController
{
    [Command("delivery_action_failure")]
    public IBotResult Fail()
    {
        State.Set("action", true);
        throw new InvalidOperationException("action failed");
    }

    [Command("custom_delivery_result")]
    public IBotResult Custom() => new CustomDeliveryResult().Finally(() => State.Set("custom_finished", true));

    [Command("delivery_state")]
    public IBotResult Save(string? args)
    {
        State.Set("action", true);
        return Reply(Message.Create<FactoryView>(nameof(FactoryView.Home)).Paragraph("Delivery state")
            .OnDelivered((receipt, _) =>
            {
                State.Set("receipt", receipt.PrimaryMessageId);
                if (args == "callback_error")
                    throw new InvalidOperationException("callback failed");
                return Task.CompletedTask;
            }))
            .Finally(token =>
            {
                Assert.False(token.IsCancellationRequested);
                if (args == "clear")
                    State.Clear();
                else
                    State.Set("finished", true);
                return Task.CompletedTask;
            });
    }
}

internal sealed record CustomDeliveryResult : IBotResult;

public sealed class DeliveryMechanismTests
{
    private static Message Screen() => Message.Create<FactoryView>(nameof(FactoryView.Home)).Paragraph("Hello");
    private static readonly MediaFile Photo = MediaFile.FromFileId(MediaType.Photo, "photo");

    [Fact]
    public async Task Background_sender_uses_structured_receipts_and_edits_all_media()
    {
        await using var bot = TestBot.Create();
        var sender = bot.Services.GetRequiredService<IBotMessageSender>();
        MessageDelivery? observed = null;
        var receipt = await sender.SendAsync(42, Screen().Photo(Photo).OnDelivered((delivery, _) =>
        {
            observed = delivery;
            return Task.CompletedTask;
        }));
        Assert.Same(receipt, observed);
        Assert.Single(receipt.AssociatedMessageIds);
        var updated = await sender.EditAsync(receipt, Screen());
        Assert.Equal(receipt.PrimaryMessageId, updated.PrimaryMessageId);
        Assert.Empty(updated.AssociatedMessageIds);
        Assert.Single(bot.Telegram.MessagesIn(42));
        var replaced = await sender.EditAsync(42, 99999, Screen());
        Assert.NotEqual(99999, replaced.PrimaryMessageId);
    }

    [Fact]
    public async Task Structured_broadcasts_use_the_registered_sender_and_support_skips()
    {
        var sender = new RecordingSender();
        await using var bot = TestBot.Create(services: services => services.AddSingleton<IBotMessageSender>(sender));
        var broadcaster = bot.Services.GetRequiredService<IBotBroadcaster>();
        var first = await broadcaster.SendAsync([1, 2, 1], Screen());
        var second = await broadcaster.SendAsync([3, 4], (chat, _) => ValueTask.FromResult(chat == 3 ? Screen() : null));
        Assert.Equal(2, first.Sent);
        Assert.Equal(1, second.Sent);
        Assert.Equal(1, second.Skipped);
        Assert.Equal(new long[] { 1, 2, 3 }, sender.Chats.Order());
    }

    [Fact]
    public async Task Background_operations_own_and_dispose_separate_scopes_even_on_failure()
    {
        var scopes = new ScopeLog();
        await using var bot = TestBot.Create(services: services =>
        {
            services.AddSingleton(scopes);
            services.AddScoped<IBotResultExecutor, ScopedExecutor>();
        });
        var sender = bot.Services.GetRequiredService<IBotMessageSender>();
        await Task.WhenAll(sender.SendAsync(1, Screen()), sender.SendAsync(2, Screen()));
        await Assert.ThrowsAsync<MessageDeliveryException>(() => sender.SendAsync(3,
            Screen().OnDelivered((_, _) => throw new InvalidOperationException("callback failed"))));
        Assert.Equal(3, scopes.Executed.Distinct().Count());
        Assert.Equal(scopes.Executed.Order(), scopes.Disposed.Order());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("api_error")]
    [InlineData("cancel")]
    [InlineData("validation_error")]
    public async Task Finally_runs_once_with_an_independent_token(string outcome)
    {
        await using var bot = TestBot.Create();
        using var cancellation = new CancellationTokenSource();
        if (outcome != "success")
            bot.Telegram.Handle<SendMessageRequest>(_ =>
            {
                if (outcome == "cancel")
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                throw new ApiRequestException("failed", 500);
            });
        var count = 0;
        var message = outcome == "validation_error" ? Screen().RawHtml("<invalid>") : Screen();
        message = message.Finally(token =>
        {
            Assert.False(token.IsCancellationRequested);
            count++;
            return Task.CompletedTask;
        });
        var send = bot.Services.GetRequiredService<IBotMessageSender>().SendAsync(42, message, cancellation.Token);
        if (outcome == "success")
            await send;
        else
            await Assert.ThrowsAnyAsync<Exception>(() => send);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Finalization_preserves_execution_and_cleanup_errors()
    {
        await using var bot = TestBot.Create();
        var context = new BotContext(new Telegram.Bot.Types.Update(), bot.Telegram, bot.Services, CancellationToken.None);
        var final = new InvalidOperationException("cleanup failed");
        var result = new SendText("no chat").Finally(() => throw final);
        var error = await Assert.ThrowsAsync<AggregateException>(() => new TelegramResultExecutor().ExecuteAsync(context, result, CancellationToken.None));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains("no chat", error.InnerExceptions[0].Message);
        Assert.Same(final, error.InnerExceptions[1]);
        var count = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new TelegramResultExecutor().ExecuteAsync(context,
            new SendText("no chat").Finally(() => count++), CancellationToken.None));
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("success", true)]
    [InlineData("callback_error", true)]
    [InlineData("api_error", false)]
    [InlineData("cancel", false)]
    public async Task State_is_saved_after_delivery_including_failure_and_cancellation(string outcome, bool delivered)
    {
        await using var bot = TestBot.Create();
        using var cancellation = new CancellationTokenSource();
        if (outcome is "api_error" or "cancel")
            bot.Telegram.Handle<SendMessageRequest>(_ =>
            {
                if (outcome == "cancel")
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                throw new ApiRequestException("failed", 500);
            });
        var user = bot.PrivateChat();
        await user.SendText("/delivery_state " + outcome, cancellation.Token);
        var state = await user.GetState();
        Assert.NotNull(state);
        Assert.True(state.Get<bool>("action"));
        Assert.True(state.Get<bool>("finished"));
        Assert.Equal(delivered, state.Data.ContainsKey("receipt"));
    }

    [Fact]
    public async Task Clearing_state_in_finalization_removes_it_from_storage()
    {
        await using var bot = TestBot.Create();
        var user = bot.PrivateChat();
        await user.SendText("/delivery_state clear");
        Assert.Null(await user.GetState());
    }

    [Fact]
    public async Task Custom_executors_receive_unwrapped_results_and_keep_state_finalization()
    {
        await using var bot = TestBot.Create(services: services => services.AddScoped<IBotResultExecutor, CustomExecutor>());
        var user = bot.PrivateChat();
        await user.SendText("/custom_delivery_result");
        var state = await user.GetState();
        Assert.True(state!.Get<bool>("custom_executed"));
        Assert.True(state.Get<bool>("custom_finished"));
    }

    [Fact]
    public async Task Parameterless_async_finalization_is_awaited()
    {
        await using var bot = TestBot.Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = bot.Services.GetRequiredService<IBotMessageSender>().SendAsync(42, Screen().Finally(async () =>
        {
            entered.SetResult();
            await release.Task;
        }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(send.IsCompleted);
        release.SetResult();
        await send;
    }

    [Fact]
    public async Task Callback_and_post_delivery_storage_errors_are_both_preserved()
    {
        await using var bot = TestBot.Create();
        var storage = new FailingStorage();
        var middleware = new Middleware.StateMiddleware(storage, bot.Services.GetRequiredService<TelegramBotBuilder>());
        var context = new BotContext(new Telegram.Bot.Types.Update
        {
            Message = new Telegram.Bot.Types.Message
            {
                Chat = new Telegram.Bot.Types.Chat { Id = 42 },
                From = new Telegram.Bot.Types.User { Id = 42, FirstName = "Test" }
            }
        }, bot.Telegram, bot.Services, CancellationToken.None);
        var result = await middleware.InvokeAsync(context, (current, _) =>
        {
            current.State!.Set("action", true);
            return Task.FromResult<IBotResult?>(new DeliverMessage(Screen().OnDelivered((_, _) =>
            {
                current.State.Set("receipt", true);
                throw new InvalidOperationException("callback failed");
            })));
        }, CancellationToken.None);
        var error = await Assert.ThrowsAsync<MessageDeliveryException>(() => ResultLifecycleExtensions.FinishAsync(
            () => new TelegramResultExecutor().ExecuteAsync(context, result!, CancellationToken.None),
            token => context.SaveStateAfterResult!(token), context));
        Assert.Contains(error.Flatten().InnerExceptions, exception => exception.Message == "callback failed");
        Assert.Contains(error.Flatten().InnerExceptions, exception => exception.Message == "storage failed");
        Assert.Equal(2, storage.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_deletes_primary_and_associated_media_even_after_callback_failure(bool callbackFails)
    {
        await using var bot = TestBot.Create();
        var scheduler = bot.Services.GetRequiredService<MessageDeletionScheduler>();
        await scheduler.StartAsync(CancellationToken.None);
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ids = new System.Collections.Concurrent.ConcurrentBag<int>();
        bot.Telegram.Handle<DeleteMessageRequest>(request =>
        {
            ids.Add(request.MessageId);
            if (ids.Count == 2)
                deleted.TrySetResult();
            return null;
        });
        MessageDelivery? receipt = null;
        var send = bot.Services.GetRequiredService<IBotMessageSender>().SendAsync(42,
            Screen().Photo(Photo).DeleteAfter(TimeSpan.FromMilliseconds(30)).OnDelivered((delivery, _) =>
            {
                receipt = delivery;
                if (callbackFails)
                    throw new InvalidOperationException("callback failed");
                return Task.CompletedTask;
            }));
        if (callbackFails)
            await Assert.ThrowsAsync<MessageDeliveryException>(() => send);
        else
            await send;
        await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync(CancellationToken.None);
        Assert.NotNull(receipt);
        Assert.Equal(receipt.AssociatedMessageIds.Prepend(receipt.PrimaryMessageId).Order(), ids.Order());
        Assert.Empty(bot.Telegram.MessagesIn(42));
    }

    [Fact]
    public void Negative_expiry_is_rejected_at_declaration_time()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Screen().DeleteAfter(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliverMessage(Screen()).DeleteAfter(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task Expiry_covers_sequences_and_legacy_results_and_continues_after_delete_errors()
    {
        await using var bot = TestBot.Create();
        var scheduler = bot.Services.GetRequiredService<MessageDeletionScheduler>();
        await scheduler.StartAsync(CancellationToken.None);
        var attempted = new System.Collections.Concurrent.ConcurrentBag<int>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bot.Telegram.Handle<DeleteMessageRequest>(request =>
        {
            attempted.Add(request.MessageId);
            if (attempted.Count == 3)
                complete.SetResult();
            throw new ApiRequestException("Forbidden", 403);
        });
        var context = new BotContext(new Telegram.Bot.Types.Update
        {
            Message = new Telegram.Bot.Types.Message { Chat = new Telegram.Bot.Types.Chat { Id = 42 } }
        }, bot.Telegram, bot.Services, CancellationToken.None);
        var result = new SendText("legacy").Then(new DeliverMessage(Screen().Photo(Photo))).DeleteAfter(TimeSpan.Zero);
        await new TelegramResultExecutor().ExecuteAsync(context, result, CancellationToken.None);
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync(CancellationToken.None);
        Assert.Equal(context.Deliveries.SelectMany(receipt => receipt.AssociatedMessageIds.Prepend(receipt.PrimaryMessageId)).Order(), attempted.Order());
    }

    private sealed class CustomExecutor : IBotResultExecutor
    {
        public Task ExecuteAsync(BotContext context, IBotResult result, CancellationToken ct)
        {
            Assert.IsType<CustomDeliveryResult>(result);
            context.State!.Set("custom_executed", true);
            return Task.CompletedTask;
        }
    }

    private sealed class ScopeLog
    {
        public System.Collections.Concurrent.ConcurrentBag<Guid> Executed { get; } = [];
        public System.Collections.Concurrent.ConcurrentBag<Guid> Disposed { get; } = [];
    }

    private sealed class ScopedExecutor(ScopeLog scopes) : IBotResultExecutor, IAsyncDisposable
    {
        private readonly Guid _id = Guid.NewGuid();
        public Task ExecuteAsync(BotContext context, IBotResult result, CancellationToken ct)
        {
            scopes.Executed.Add(_id);
            return new TelegramResultExecutor().ExecuteAsync(context, result, ct);
        }
        public ValueTask DisposeAsync()
        {
            scopes.Disposed.Add(_id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingStorage : IStateStorage
    {
        public int Saves { get; private set; }
        public ValueTask<ChatState?> GetAsync(StateKey key, CancellationToken ct) => ValueTask.FromResult<ChatState?>(null);
        public ValueTask RemoveAsync(StateKey key, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask SaveAsync(StateKey key, ChatState state, TimeSpan timeToLive, CancellationToken ct)
        {
            Assert.False(ct.IsCancellationRequested);
            if (++Saves == 2)
                throw new InvalidOperationException("storage failed");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSender : IBotMessageSender
    {
        public System.Collections.Concurrent.ConcurrentBag<long> Chats { get; } = [];
        public Task<MessageDelivery> SendAsync(long chatId, Message message, CancellationToken ct = default)
        {
            Chats.Add(chatId);
            return Task.FromResult(new MessageDelivery(chatId, 123, [], false));
        }
        public Task<MessageDelivery> EditAsync(long chatId, int messageId, Message message, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MessageDelivery> EditAsync(MessageDelivery previous, Message message, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
