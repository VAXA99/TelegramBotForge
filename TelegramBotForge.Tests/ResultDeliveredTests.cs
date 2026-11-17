using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Media;
using TelegramBotForge.Results;
using TelegramBotForge.Testing;
using Message = TelegramBotForge.Views.Message;

namespace TelegramBotForge.Tests;

public sealed class ResultDeliveredTests
{
    private static BotContext Context(BotTestHost bot, CancellationToken ct = default) => new(new Update
    {
        Message = new Telegram.Bot.Types.Message { Chat = new Chat { Id = 42 } }
    }, bot.Telegram, bot.Services, ct);

    private static Message Screen() => Message.Create<FactoryView>(nameof(FactoryView.Home)).Paragraph("Result delivery");

    [Fact]
    public async Task SendText_with_arbitrary_keyboard_reports_actual_receipt()
    {
        await using var bot = TestBot.Create();
        var context = Context(bot);
        var keyboard = new ReplyKeyboardMarkup(new[] { new[] { KeyboardButton.WithRequestUsers("Users", new KeyboardButtonRequestUsers(17) { MaxQuantity = 3 }) } });
        var observed = new List<MessageDelivery>();
        var result = new SendText("Choose users", keyboard).OnDelivered((receipt, _) =>
        {
            observed.Add(receipt);
            return Task.CompletedTask;
        });

        await new TelegramResultExecutor().ExecuteAsync(context, result, CancellationToken.None);

        var receipt = Assert.Single(observed);
        Assert.Same(Assert.Single(context.Deliveries), receipt);
        Assert.Equal(42, receipt.ChatId);
        Assert.Equal(Assert.Single(bot.Telegram.MessagesIn(42)).Id, receipt.PrimaryMessageId);
        Assert.Same(keyboard, Assert.Single(bot.Telegram.RequestsOf<SendMessageRequest>()).ReplyMarkup);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("replace")]
    [InlineData("unchanged")]
    public async Task Editing_reports_actual_edited_or_replacement_id(string outcome)
    {
        await using var bot = TestBot.Create();
        var context = Context(bot);
        var executor = new TelegramResultExecutor();
        await executor.ExecuteAsync(context, new SendText("Original"), CancellationToken.None);
        var original = Assert.Single(context.Deliveries);
        if (outcome == "unchanged")
            bot.Telegram.Handle<EditMessageTextRequest>(_ => throw new ApiRequestException("Bad Request: message is not modified", 400));
        var observed = new List<MessageDelivery>();
        var result = new EditText("Updated", MessageId: outcome == "replace" ? 99999 : original.PrimaryMessageId)
            .OnDelivered((receipt, _) => { observed.Add(receipt); return Task.CompletedTask; });

        await executor.ExecuteAsync(context, result, CancellationToken.None);

        var receipt = Assert.Single(observed);
        Assert.Same(context.Deliveries.Last(), receipt);
        Assert.Equal(bot.Telegram.MessagesIn(42).Last().Id, receipt.PrimaryMessageId);
        if (outcome == "replace")
            Assert.NotEqual(original.PrimaryMessageId, receipt.PrimaryMessageId);
        else
            Assert.Equal(original.PrimaryMessageId, receipt.PrimaryMessageId);
    }

    [Fact]
    public async Task Failed_delivery_and_rolled_back_media_do_not_invoke_callbacks()
    {
        await using var bot = TestBot.Create();
        var context = Context(bot);
        var failure = new ApiRequestException("send failed", 500);
        bot.Telegram.Handle<SendMessageRequest>(_ => throw failure);
        var delivered = 0;
        var finished = 0;
        var message = Screen().Photo(MediaFile.FromFileId(MediaType.Photo, "photo"));
        var result = new DeliverMessage(message).OnDelivered((_, _) => { delivered++; return Task.CompletedTask; }).Finally(() => finished++);

        var error = await Assert.ThrowsAsync<ApiRequestException>(() => new TelegramResultExecutor().ExecuteAsync(context, result, CancellationToken.None));

        Assert.Same(failure, error);
        Assert.Equal(0, delivered);
        Assert.Equal(1, finished);
        Assert.Empty(context.Deliveries);
        Assert.Empty(bot.Telegram.MessagesIn(42));
        Assert.Single(bot.Telegram.RequestsOf<DeleteMessageRequest>());
    }

    [Fact]
    public async Task MultiResult_observes_each_receipt_once_in_order_and_ignores_empty_results()
    {
        await using var bot = TestBot.Create();
        var context = Context(bot);
        var observed = new List<MessageDelivery>();
        var order = new List<string>();
        var first = new SendText("First").OnDelivered((_, _) => { order.Add("inner"); return Task.CompletedTask; });
        var result = new MultiResult(first, new MultiResult(), new DeliverMessage(Screen().Photo(MediaFile.FromFileId(MediaType.Photo, "photo"))))
            .Finally(() => order.Add("finished"))
            .OnDelivered((receipt, _) => { observed.Add(receipt); order.Add("outer"); return Task.CompletedTask; });

        await new TelegramResultExecutor().ExecuteAsync(context, result, CancellationToken.None);

        Assert.Equal(context.Deliveries, observed);
        Assert.Equal(2, observed.Count);
        Assert.Single(observed[1].AssociatedMessageIds);
        Assert.Equal(new[] { "inner", "outer", "outer", "finished" }, order);
    }

    [Fact]
    public async Task Failed_later_item_keeps_earlier_notifications_and_original_send_error()
    {
        await using var bot = TestBot.Create();
        var context = Context(bot);
        var failure = new ApiRequestException("send failed", 500);
        bot.Telegram.Handle<SendMessageRequest>(request => request.Text == "Second" ? throw failure : null);
        var observed = new List<MessageDelivery>();
        var result = new MultiResult(new SendText("First"), new SendText("Second"), new SendText("Not reached"))
            .OnDelivered((receipt, _) => { observed.Add(receipt); return Task.CompletedTask; });

        var error = await Assert.ThrowsAsync<ApiRequestException>(() => new TelegramResultExecutor().ExecuteAsync(context, result, CancellationToken.None));

        Assert.Same(failure, error);
        Assert.Same(Assert.Single(context.Deliveries), Assert.Single(observed));
        Assert.Equal(2, bot.Telegram.RequestsOf<SendMessageRequest>().Count);
    }

    [Fact]
    public async Task Callback_api_error_is_post_delivery_and_cannot_trigger_edit_replacement()
    {
        await using var bot = TestBot.Create();
        var context = Context(bot);
        var executor = new TelegramResultExecutor();
        await executor.ExecuteAsync(context, new SendText("Original"), CancellationToken.None);
        var original = Assert.Single(context.Deliveries);
        bot.Telegram.ClearRequests();
        var failure = new ApiRequestException("Bad Request: message to edit not found", 400);
        var finished = 0;
        var result = new EditText("Updated", MessageId: original.PrimaryMessageId)
            .OnDelivered((_, _) => throw failure).Finally(() => finished++);

        var error = await Assert.ThrowsAsync<MessageDeliveryException>(() => executor.ExecuteAsync(context, result, CancellationToken.None));

        Assert.Equal(original.PrimaryMessageId, error.Delivery.PrimaryMessageId);
        Assert.Same(failure, Assert.Single(error.Flatten().InnerExceptions));
        Assert.Single(bot.Telegram.RequestsOf<EditMessageTextRequest>());
        Assert.Empty(bot.Telegram.RequestsOf<SendMessageRequest>());
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task All_receipt_observers_run_even_when_a_structured_callback_failed()
    {
        await using var bot = TestBot.Create();
        var context = Context(bot);
        var innerError = new InvalidOperationException("message callback failed");
        var outerError = new InvalidOperationException("result callback failed");
        var observed = new List<MessageDelivery>();
        var result = new DeliverMessage(Screen().OnDelivered((_, _) => throw innerError))
            .OnDelivered((_, _) => throw outerError)
            .OnDelivered((receipt, _) => { observed.Add(receipt); return Task.CompletedTask; });

        var error = await Assert.ThrowsAsync<MessageDeliveryException>(() => new TelegramResultExecutor().ExecuteAsync(context, result, CancellationToken.None));

        Assert.Same(Assert.Single(observed), error.Delivery);
        Assert.Contains(innerError, error.Flatten().InnerExceptions);
        Assert.Contains(outerError, error.Flatten().InnerExceptions);
    }

    [Fact]
    public async Task Deleting_a_message_does_not_notify_or_reuse_an_earlier_receipt()
    {
        await using var bot = TestBot.Create();
        var context = Context(bot);
        var executor = new TelegramResultExecutor();
        await executor.ExecuteAsync(context, new SendText("Original"), CancellationToken.None);
        var delivered = 0;
        var result = new DeleteMessage(Assert.Single(context.Deliveries).PrimaryMessageId)
            .OnDelivered((_, _) => { delivered++; return Task.CompletedTask; });
        await executor.ExecuteAsync(context, result, CancellationToken.None);
        Assert.Equal(0, delivered);
        Assert.Empty(bot.Telegram.MessagesIn(42));
    }
}
