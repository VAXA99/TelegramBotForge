using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Controllers;
using TelegramBotForge.Input;
using TelegramBotForge.Results;
using TelegramBotForge.State;
using TelegramBotForge.Testing;
using Message = TelegramBotForge.Views.Message;

namespace TelegramBotForge.Tests;

public sealed class InputTestOptions
{
    public TemporaryInputCommandPolicy Commands { get; set; }
    public string? CallbackFailure { get; set; }
    public CancellationTokenSource? Cancellation { get; set; }
    public bool Media { get; set; }
    public List<Guid> Completed { get; } = [];
}

public sealed class InputTestController(ITemporaryInput input, InputTestOptions options) : BotController
{
    [Command("input_previous")]
    public IBotResult Previous()
    {
        State.Current = "previous";
        State.Set("domain", "retained");
        return Reply("Previous");
    }

    [Command("input_users")]
    public IBotResult Users() => Open(TemporaryInputKind.Users);

    [Command("input_location")]
    public IBotResult Location() => Open(TemporaryInputKind.Location);

    private IBotResult Open(TemporaryInputKind kind) => input.Begin("temporary-input", kind, request =>
    {
        if (options.CallbackFailure == "factory")
            throw new InvalidOperationException("factory");
        var prompt = Message.Create<FactoryView>(nameof(FactoryView.Home)).Paragraph("Input prompt");
        if (options.Media)
            prompt = prompt.Photo(TelegramBotForge.Media.MediaFile.FromFileId(TelegramBotForge.Media.MediaType.Photo, "photo"));
        prompt = kind == TemporaryInputKind.Users
            ? prompt.RequestUsers("Select", new KeyboardButtonRequestUsers(request.RequestId!.Value) { MaxQuantity = 3 })
            : prompt.RequestLocation("Location");
        return prompt.OnDelivered((_, _) =>
        {
            Assert.NotNull(input.Current!.Prompt);
            State.Set("input.delivered", true);
            if (options.CallbackFailure == "delivered")
                throw new InvalidOperationException("delivered");
            if (options.CallbackFailure == "cancel")
            {
                options.Cancellation!.Cancel();
                throw new OperationCanceledException(options.Cancellation.Token);
            }
            return Task.CompletedTask;
        }).Finally(() =>
        {
            State.Set("input.finished", true);
            if (options.CallbackFailure == "finally")
                throw new InvalidOperationException("finally");
        });
    }, new TemporaryInputOptions(TimeSpan.FromMinutes(1), "Expired or interrupted", options.Commands));

    [State("temporary-input"), UsersShared, Location]
    public IBotResult Answer()
    {
        if (!input.TryMatch(out var request))
            return new MultiResult();
        options.Completed.Add(request.Id);
        State.Set("answer", true);
        return input.Complete(request.Id, "Completed").Then(input.Complete(request.Id, "Repeated"));
    }

    [Callback("input_cancel:{id}")]
    public IBotResult Cancel(Guid id) => input.Cancel(id, "Cancelled").Then(input.Cancel(id, "Repeated"));

    [Command("input_status")]
    public IBotResult Status() => Reply(input.Current is null ? "No input" : "Active input");
}

public sealed class TemporaryInputTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CheckingStorage(TimeProvider clock) : IStateStorage
    {
        private readonly InMemoryStateStorage _inner = new(clock);
        public ValueTask<ChatState?> GetAsync(StateKey key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return _inner.GetAsync(key, ct);
        }
        public ValueTask SaveAsync(StateKey key, ChatState state, TimeSpan timeToLive, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return _inner.SaveAsync(key, state, timeToLive, ct);
        }
        public ValueTask RemoveAsync(StateKey key, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return _inner.RemoveAsync(key, ct);
        }
    }

    private static BotTestHost Create(InputTestOptions? options = null, TimeProvider? clock = null) => TestBot.Create(
        services: services =>
        {
            services.AddSingleton(options ?? new InputTestOptions());
            if (clock is not null)
                services.AddSingleton(clock);
            services.AddSingleton<IStateStorage>(provider => new CheckingStorage(provider.GetRequiredService<TimeProvider>()));
        }, bot: builder => builder.UseMiddleware<OuterDeliveryFinallyMiddleware>());

    private static InputSession Session(ChatState? state) => Assert.IsType<InputSession>(state!.Get<InputSession>(TemporaryInput.SessionKey));
    private static Task<UpdateResult> Answer(TestChat user, int id, CancellationToken ct = default) => user.SendMessage(
        message => message.UsersShared = new UsersShared { RequestId = id, Users = [new SharedUser { UserId = 123 }] }, ct);

    [Fact]
    public async Task Begin_commits_receipt_and_callbacks_then_completion_restores_previous_state_and_data()
    {
        var options = new InputTestOptions();
        await using var bot = Create(options);
        var user = bot.PrivateChat();
        await user.SendText("/input_previous");
        bot.Telegram.Handle<SendMessageRequest>(request =>
        {
            if (request.Text == "Input prompt")
            {
                var pending = bot.Services.GetRequiredService<IStateStorage>().GetAsync(
                    new StateKey(user.Chat.Id, user.User.Id), CancellationToken.None).GetAwaiter().GetResult();
                Assert.Null(Session(pending).Prompt);
                Assert.Equal("temporary-input", pending!.Current);
            }
            return null;
        });
        await user.SendText("/input_users");
        var state = (await user.GetState())!;
        var session = Session(state);
        Assert.Equal("temporary-input", state.Current);
        Assert.Equal("previous", session.PreviousState);
        Assert.Equal(user.User.Id, session.UserId);
        Assert.Equal(user.Chat.Id, session.ChatId);
        Assert.Equal(user.LastMessage!.Id, session.Prompt!.PrimaryMessageId);
        Assert.True(state.Get<bool>("input.delivered"));
        Assert.True(state.Get<bool>("input.finished"));
        Assert.True(state.Get<bool>("outer_finished"));
        var markup = Assert.IsType<ReplyKeyboardMarkup>(bot.Telegram.RequestsOf<SendMessageRequest>().Last().ReplyMarkup);
        Assert.Equal(session.RequestId, Assert.Single(Assert.Single(markup.Keyboard)).RequestUsers!.RequestId);

        var result = await Answer(user, session.RequestId!.Value);

        Assert.Equal("Completed", result.Text);
        Assert.IsType<ReplyKeyboardRemove>(Assert.Single(result.Of<SendMessageRequest>()).ReplyMarkup);
        Assert.Equal(session.Prompt.PrimaryMessageId, Assert.Single(result.Of<DeleteMessageRequest>()).MessageId);
        state = (await user.GetState())!;
        Assert.Equal("previous", state.Current);
        Assert.Equal("retained", state.Get<string>("domain"));
        Assert.True(state.Get<bool>("answer"));
        Assert.Null(state.Get<InputSession>(TemporaryInput.SessionKey));
        Assert.Equal(new[] { session.Id }, options.Completed);
        Assert.True((await Answer(user, session.RequestId.Value)).IsEmpty);
        Assert.Single(options.Completed);
    }

    [Fact]
    public async Task Cancel_and_restart_ignore_old_answers_and_old_windows_and_reject_second_active_request()
    {
        await using var bot = Create();
        var user = bot.PrivateChat();
        await user.SendText("/input_previous");
        await user.SendText("/input_users");
        var first = Session(await user.GetState());
        await user.SendText("/input_users");
        Assert.Equal(first.Id, Session(await user.GetState()).Id);
        Assert.Single(bot.Telegram.RequestsOf<SendMessageRequest>(), request => request.Text == "Input prompt");
        var cancelled = await user.SendCallback($"input_cancel:{first.Id}");
        Assert.Equal("Cancelled", cancelled.Text);
        Assert.Single(cancelled.Of<DeleteMessageRequest>());
        Assert.Equal("previous", (await user.GetState())!.Current);
        await user.SendText("/input_users");
        var second = Session(await user.GetState());
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.True((await Answer(user, first.RequestId!.Value)).IsEmpty);
        var staleClose = await user.SendCallback($"input_cancel:{first.Id}");
        Assert.Empty(staleClose.Of<DeleteMessageRequest>());
        Assert.Empty(staleClose.Of<SendMessageRequest>());
        Assert.Equal(second.Id, Session(await user.GetState()).Id);
        Assert.Equal("Completed", (await Answer(user, second.RequestId!.Value)).Text);
    }

    [Fact]
    public async Task Expiry_rejects_response_and_cleans_up_before_next_prompt()
    {
        var clock = new Clock();
        var options = new InputTestOptions();
        await using var bot = Create(options, clock);
        var user = bot.PrivateChat();
        await user.SendText("/input_previous");
        await user.SendText("/input_users");
        var first = Session(await user.GetState());
        clock.Now += TimeSpan.FromMinutes(2);
        var expired = await Answer(user, first.RequestId!.Value);
        Assert.Empty(options.Completed);
        Assert.Equal("Expired or interrupted", expired.Text);
        Assert.Equal("previous", (await user.GetState())!.Current);
        Assert.Null((await user.GetState())!.Get<InputSession>(TemporaryInput.SessionKey));
        await user.SendText("/input_users");
        clock.Now += TimeSpan.FromMinutes(2);
        var reopened = await user.SendText("/input_users");
        Assert.Equal(new[] { "Expired or interrupted", "Input prompt" }, reopened.Of<SendMessageRequest>().Select(request => request.Text));
        Assert.IsType<ReplyKeyboardMarkup>(reopened.Of<SendMessageRequest>().Last().ReplyMarkup);
        Assert.NotEqual(first.RequestId, Session(await user.GetState()).RequestId);
    }

    [Theory]
    [InlineData(TemporaryInputCommandPolicy.Preserve, "Active input")]
    [InlineData(TemporaryInputCommandPolicy.Cancel, "No input")]
    public async Task Commands_interrupt_only_with_explicit_policy(TemporaryInputCommandPolicy policy, string expected)
    {
        await using var bot = Create(new InputTestOptions { Commands = policy });
        var user = bot.PrivateChat();
        await user.SendText("/input_users");
        var original = Session(await user.GetState());
        await user.SendText("/input_status@other_bot");
        Assert.Equal(original.Id, Session(await user.GetState()).Id);
        var result = await user.SendText("/input_status");
        Assert.Equal(expected, result.Of<SendMessageRequest>().Last().Text);
        Assert.Equal(policy == TemporaryInputCommandPolicy.Cancel, result.Of<DeleteMessageRequest>().Any());
    }

    [Fact]
    public async Task Location_requires_reply_to_actual_prompt_and_cannot_complete_another_request()
    {
        await using var bot = Create();
        var user = bot.PrivateChat();
        await user.SendText("/input_location");
        var session = Session(await user.GetState());
        Assert.Null(session.RequestId);
        Assert.True((await user.SendLocation(10, 20)).IsEmpty);
        Assert.True((await user.SendMessage(message =>
        {
            message.Location = new Location { Latitude = 10, Longitude = 20 };
            message.ReplyToMessage = new Telegram.Bot.Types.Message { Id = session.Prompt!.PrimaryMessageId - 1, Chat = user.Chat };
        })).IsEmpty);
        Assert.Equal("Completed", (await user.SendMessage(message =>
        {
            message.Location = new Location { Latitude = 10, Longitude = 20 };
            message.ReplyToMessage = new Telegram.Bot.Types.Message { Id = session.Prompt!.PrimaryMessageId, Chat = user.Chat };
        })).Text);
    }

    [Fact]
    public async Task Requests_are_isolated_by_user_and_chat()
    {
        await using var bot = Create();
        var first = bot.GroupChat(-100, userId: 1);
        var otherUser = bot.GroupChat(-100, userId: 2);
        var otherChat = bot.GroupChat(-200, userId: 1);
        foreach (var user in new[] { first, otherUser, otherChat })
            await user.SendText("/input_users");
        var current = Session(await first.GetState());
        foreach (var user in new[] { otherUser, otherChat })
        {
            var own = Session(await user.GetState());
            await user.SendCallback($"input_cancel:{current.Id}");
            Assert.Equal(own.Id, Session(await user.GetState()).Id);
        }
        await first.SendCallback($"input_cancel:{current.Id}");
        Assert.Null((await first.GetState())!.Get<InputSession>(TemporaryInput.SessionKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_delivery_rolls_back_but_saves_finalizers(bool cancel)
    {
        await using var bot = Create();
        var user = bot.PrivateChat();
        await user.SendText("/input_previous");
        using var cancellation = new CancellationTokenSource();
        bot.Telegram.Handle<SendMessageRequest>(_ =>
        {
            if (cancel)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            throw new ApiRequestException("Unavailable", 500);
        });
        await user.SendText("/input_users", cancellation.Token);
        var state = (await user.GetState())!;
        Assert.Equal("previous", state.Current);
        Assert.Null(state.Get<InputSession>(TemporaryInput.SessionKey));
        Assert.Equal("retained", state.Get<string>("domain"));
        Assert.True(state.Get<bool>("input.finished"));
        Assert.True(state.Get<bool>("outer_finished"));
        Assert.False(state.Get<bool>("input.delivered"));
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("finally")]
    [InlineData("cancel")]
    public async Task Confirmed_prompt_survives_callback_errors_and_is_not_sent_again(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        await using var bot = Create(new InputTestOptions { CallbackFailure = failure, Cancellation = cancellation });
        var user = bot.PrivateChat();
        await user.SendText("/input_users", cancellation.Token);
        var state = (await user.GetState())!;
        var session = Session(state);
        Assert.NotNull(session.Prompt);
        Assert.True(state.Get<bool>("input.delivered"));
        Assert.True(state.Get<bool>("input.finished"));
        Assert.True(state.Get<bool>("outer_finished"));
        await user.SendText("/input_users");
        Assert.Equal(session.Id, Session(await user.GetState()).Id);
        Assert.Single(bot.Telegram.RequestsOf<SendMessageRequest>(), request => request.Text == "Input prompt");
    }

    [Fact]
    public async Task Completion_cleanup_ignores_delivery_cancellation_and_removes_associated_media()
    {
        using var cancellation = new CancellationTokenSource();
        await using var bot = Create(new InputTestOptions { Media = true });
        var user = bot.PrivateChat();
        await user.SendText("/input_users");
        var session = Session(await user.GetState());
        Assert.Single(session.Prompt!.AssociatedMessageIds);
        bot.Telegram.Handle<DeleteMessageRequest>(_ =>
        {
            cancellation.Cancel();
            return null;
        });

        var result = await Answer(user, session.RequestId!.Value, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal("Completed", result.Text);
        Assert.Equal(session.Prompt.AssociatedMessageIds.Prepend(session.Prompt.PrimaryMessageId).Order(),
            result.Of<DeleteMessageRequest>().Select(request => request.MessageId).Order());
        Assert.Null((await user.GetState())!.Get<InputSession>(TemporaryInput.SessionKey));
        Assert.True((await user.GetState())!.Get<bool>("outer_finished"));
    }

    [Fact]
    public async Task Stored_receipt_recovers_after_restart_and_pending_entries_are_never_opened()
    {
        var storage = new InMemoryStateStorage();
        var clock = new Clock();
        var options = new InputTestOptions();
        BotTestHost Host() => TestBot.Create(services: services =>
        {
            services.AddSingleton(options);
            services.AddSingleton<IStateStorage>(storage);
            services.AddSingleton<TimeProvider>(clock);
        });
        InputSession session;
        await using (var first = Host())
        {
            var user = first.PrivateChat(1234);
            await user.SendText("/input_users");
            session = Session(await user.GetState());
        }
        await using var restarted = Host();
        var returning = restarted.PrivateChat(1234);
        Assert.Equal("Completed", (await Answer(returning, session.RequestId!.Value)).Text);
        Assert.Single(options.Completed);

        await returning.SendText("/input_users");
        var state = (await returning.GetState())!;
        var pending = Session(state) with { Prompt = null };
        state.Set(TemporaryInput.SessionKey, pending);
        await storage.SaveAsync(new StateKey(returning.Chat.Id, returning.User.Id), state, TimeSpan.FromHours(1), CancellationToken.None);
        Assert.Equal("No input", (await returning.SendText("/input_status")).Text);
        Assert.Null((await returning.GetState())!.Get<InputSession>(TemporaryInput.SessionKey));
    }

    [Fact]
    public async Task Independent_state_change_does_not_restore_over_new_scenario_or_remove_its_keyboard()
    {
        await using var bot = Create();
        var user = bot.PrivateChat();
        await user.SendText("/input_users");
        var state = (await user.GetState())!;
        state.Current = "new-scenario";
        state.Set("new-data", "preserved");
        await bot.Services.GetRequiredService<IStateStorage>().SaveAsync(
            new StateKey(user.Chat.Id, user.User.Id), state, TimeSpan.FromHours(1), CancellationToken.None);

        var result = await user.SendText("/input_status");

        Assert.Equal("No input", result.Text);
        Assert.Null(Assert.Single(result.Of<SendMessageRequest>()).ReplyMarkup);
        Assert.Single(result.Of<DeleteMessageRequest>());
        state = (await user.GetState())!;
        Assert.Equal("new-scenario", state.Current);
        Assert.Equal("preserved", state.Get<string>("new-data"));
        Assert.Null(state.Get<InputSession>(TemporaryInput.SessionKey));
    }

    [Fact]
    public async Task Cleanup_errors_do_not_repeat_business_action_or_reopen_request()
    {
        var options = new InputTestOptions();
        await using var bot = Create(options);
        var user = bot.PrivateChat();
        await user.SendText("/input_users");
        var session = Session(await user.GetState());
        bot.Telegram.Handle<DeleteMessageRequest>(_ => throw new ApiRequestException("Unavailable", 500));
        bot.Telegram.Handle<SendMessageRequest>(_ => throw new ApiRequestException("Unavailable", 500));
        await Answer(user, session.RequestId!.Value);
        Assert.Null((await user.GetState())!.Get<InputSession>(TemporaryInput.SessionKey));
        Assert.True((await Answer(user, session.RequestId.Value)).IsEmpty);
        Assert.Single(options.Completed);
        Assert.Single(bot.Telegram.RequestsOf<DeleteMessageRequest>());
        Assert.Single(bot.Telegram.RequestsOf<SendMessageRequest>(), request => request.Text == "Input prompt");
    }

    [Fact]
    public async Task Prompt_factory_failure_preserves_previous_scenario_without_opening_or_sending()
    {
        await using var bot = Create(new InputTestOptions { CallbackFailure = "factory" });
        var user = bot.PrivateChat();
        await user.SendText("/input_previous");
        await user.SendText("/input_users");
        var state = (await user.GetState())!;
        Assert.Equal("previous", state.Current);
        Assert.Equal("retained", state.Get<string>("domain"));
        Assert.Null(state.Get<InputSession>(TemporaryInput.SessionKey));
        Assert.DoesNotContain(bot.Telegram.RequestsOf<SendMessageRequest>(), request => request.Text == "Input prompt");
    }

    [Fact]
    public void Invalid_options_are_rejected_without_an_operation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemporaryInputOptions(TimeSpan.Zero, "Expired"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemporaryInputOptions(TimeSpan.FromSeconds(-1), "Expired"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemporaryInputOptions(TimeSpan.FromDays(50), "Expired"));
        Assert.Throws<ArgumentException>(() => new TemporaryInputOptions(TimeSpan.FromMinutes(1), " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemporaryInputOptions(TimeSpan.FromMinutes(1), "Expired", (TemporaryInputCommandPolicy)99));
    }
}
