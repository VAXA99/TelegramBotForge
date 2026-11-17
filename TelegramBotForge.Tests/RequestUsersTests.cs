using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Results;
using TelegramBotForge.Views;

namespace TelegramBotForge.Tests;

public sealed class RequestUsersTests
{
    private static Message Screen() => Message.Create<FactoryView>(nameof(FactoryView.Home)).Paragraph("Select users");

    private static KeyboardButtonRequestUsers Request(bool? bots, bool? premium) => new(-17)
    {
        MaxQuantity = 10,
        UserIsBot = bots,
        UserIsPremium = premium,
        RequestName = true,
        RequestUsername = false,
        RequestPhoto = true
    };

    private static KeyboardButtonRequestUsers Users(Message message) =>
        Assert.Single(Assert.Single(Assert.IsType<ReplyKeyboardMarkup>(MessageEvaluation.Evaluate(message).Message.Keyboard).Keyboard)).RequestUsers!;

    private static void AssertRequest(KeyboardButtonRequestUsers request, bool? bots, bool? premium)
    {
        Assert.Equal(-17, request.RequestId);
        Assert.Equal(10, request.MaxQuantity);
        Assert.Equal(bots, request.UserIsBot);
        Assert.Equal(premium, request.UserIsPremium);
        Assert.True(request.RequestName);
        Assert.False(request.RequestUsername);
        Assert.True(request.RequestPhoto);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Structured_delivery_preserves_all_request_fields(bool? bots, bool? premium)
    {
        await using var bot = TestBot.Create();
        var options = Request(bots, premium);
        var receipt = await bot.Services.GetRequiredService<IBotMessageSender>().SendAsync(42, Screen().RequestUsers("Select", options));

        var request = Assert.Single(bot.Telegram.RequestsOf<SendMessageRequest>());
        var keyboard = Assert.IsType<ReplyKeyboardMarkup>(request.ReplyMarkup);
        var button = Assert.Single(Assert.Single(keyboard.Keyboard));
        Assert.Equal("Select", button.Text);
        AssertRequest(button.RequestUsers!, bots, premium);
        Assert.NotSame(options, button.RequestUsers);
        Assert.True(keyboard.ResizeKeyboard);
        Assert.True(keyboard.OneTimeKeyboard);
        Assert.Equal(Assert.Single(bot.Telegram.MessagesIn(42)).Id, receipt.PrimaryMessageId);
    }

    [Fact]
    public void Immutable_messages_copy_request_options_on_declaration_and_evaluation()
    {
        var options = Request(false, true);
        var message = Screen().RequestUsers("Select", options);
        options.RequestId = 99;
        options.MaxQuantity = 1;
        options.UserIsBot = true;
        options.UserIsPremium = false;
        options.RequestName = false;
        options.RequestUsername = true;
        options.RequestPhoto = false;
        var first = Users(message);
        AssertRequest(first, false, true);
        first.RequestId = 123;
        first.MaxQuantity = 2;
        first.RequestPhoto = false;
        AssertRequest(Users(message.Paragraph("More")), false, true);
    }

    [Fact]
    public void Legacy_builder_preserves_options_and_copies_the_input()
    {
        var options = Request(true, false);
        var builder = new MessageBuilder("RequestUsers", false, _ => { }).Text("Select").RequestUsers("Users", options);
        options.RequestId = 999;
        var keyboard = Assert.IsType<ReplyKeyboardMarkup>(builder.Build().Keyboard);
        AssertRequest(Assert.Single(Assert.Single(keyboard.Keyboard)).RequestUsers!, true, false);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Invalid_quantity_is_rejected_at_declaration(int quantity)
    {
        var request = new KeyboardButtonRequestUsers(1) { MaxQuantity = quantity };
        Assert.Throws<ArgumentOutOfRangeException>(() => Screen().RequestUsers("Users", request));
    }

    [Fact]
    public void Existing_keyboard_rules_and_unique_request_ids_are_enforced()
    {
        var request = Request(null, null);
        Assert.Throws<ArgumentNullException>(() => Screen().RequestUsers("Users", null!));
        Assert.Throws<ArgumentException>(() => Screen().RequestUsers(" ", request));
        Assert.Throws<InvalidOperationException>(() => MessageEvaluation.Evaluate(new FactoryView(new Counter()).RichScreen().RequestUsers("Users", request)));
        Assert.Throws<InvalidOperationException>(() => MessageEvaluation.Evaluate(Screen().Row(Buttons.Callback("Inline")).RequestUsers("Users", request)));
        Assert.Throws<InvalidOperationException>(() => MessageEvaluation.Evaluate(Screen().RequestUsers("First", request).RequestUsers("Second", request)));
    }

    [Fact]
    public async Task Reply_keyboard_cannot_be_used_for_a_structured_edit()
    {
        await using var bot = TestBot.Create();
        var sender = bot.Services.GetRequiredService<IBotMessageSender>();
        var previous = await sender.SendAsync(42, Screen());
        bot.Telegram.ClearRequests();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.EditAsync(previous, Screen().RequestUsers("Users", Request(false, null))));

        Assert.Empty(bot.Telegram.Requests);
    }
}
