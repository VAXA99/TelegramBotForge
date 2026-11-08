using Telegram.Bot.Requests;
using TelegramBotForge.Configurations;
using Microsoft.Extensions.DependencyInjection;

namespace TelegramBotForge.Tests;

public sealed class RoutingTests
{
    [Fact]
    public async Task Command_without_arguments()
    {
        await using var bot = TestBot.Create();
        Assert.Equal("Hello!", (await bot.PrivateChat().SendText("/start")).Text);
    }

    [Fact]
    public async Task Deep_link_payload_is_bound_to_args()
    {
        await using var bot = TestBot.Create();
        Assert.Equal("Invited by ref_42", (await bot.PrivateChat().SendText("/start ref_42")).Text);
    }

    [Theory]
    [InlineData("/echo a b  c", "a|b|c")]
    [InlineData("/echo", "(nothing)")]
    [InlineData("/echo@test_bot one", "one")]
    [InlineData("/ECHO x", "x")]
    public async Task Arguments_are_split_into_an_array(string text, string expected)
    {
        await using var bot = TestBot.Create();
        Assert.Equal(expected, (await bot.PrivateChat().SendText(text)).Text);
    }

    [Fact]
    public async Task Command_for_another_bot_is_ignored()
    {
        await using var bot = TestBot.Create();
        var result = await bot.GroupChat().SendText("/start@other_bot");
        Assert.True(result.IsEmpty, result.ToString());
    }

    [Fact]
    public async Task Unknown_command_gets_the_built_in_answer()
    {
        await using var bot = TestBot.Create();
        Assert.Equal("Unknown command", (await bot.PrivateChat().SendText("/nope")).Text);
    }

    [Fact]
    public async Task Pressing_a_button_edits_the_message()
    {
        await using var bot = TestBot.Create();
        var user = bot.PrivateChat();

        await user.SendText("/menu");
        var result = await user.PressButton("Catalog");

        Assert.Equal("Page 1", result.Text);
        Assert.Single(user.Messages);
        Assert.Equal("Page 1", user.LastMessage!.Text);
        Assert.NotNull(result.CallbackAnswer);
    }

    [Fact]
    public async Task Form_steps_follow_the_state()
    {
        await using var bot = TestBot.Create();
        var user = bot.PrivateChat();

        Assert.Equal("What's your name?", (await user.SendText("/signup")).Text);
        Assert.Equal("How old are you?", (await user.SendText("Ann")).Text);
        Assert.Equal("Please send a number.", (await user.SendText("old")).Text);
        Assert.NotNull(await user.GetState());
        Assert.Equal("Thanks, Ann!", (await user.SendText("30")).Text);
        Assert.Null(await user.GetState());
    }

    [Fact]
    public async Task Chat_type_picks_the_action()
    {
        await using var bot = TestBot.Create();
        Assert.Equal("private", (await bot.PrivateChat().SendText("/where")).Text);
        Assert.Equal("group", (await bot.GroupChat().SendText("/where")).Text);
    }

    [Fact]
    public async Task Chat_type_mismatch_falls_through_to_unknown_command()
    {
        await using var bot = TestBot.Create();
        Assert.Equal("Unknown command", (await bot.PrivateChat().SendText("/poll")).Text);
    }
}

public sealed class FilterTests
{
    [Fact]
    public async Task Filter_lets_admins_through()
    {
        await using var bot = TestBot.Create();
        Assert.Equal("Banned 42", (await bot.PrivateChat(userId: TestBot.Admin).SendText("/ban 42")).Text);
    }

    [Fact]
    public async Task Filter_stops_other_users()
    {
        await using var bot = TestBot.Create();
        Assert.Equal("Admins only.", (await bot.PrivateChat().SendText("/ban 42")).Text);
    }

    [Fact]
    public async Task Controller_filter_runs_before_action_filter()
    {
        await using var bot = TestBot.Create();
        Assert.Equal("ALL GOOD", (await bot.PrivateChat(userId: TestBot.Admin).SendText("/stats")).Text);
        Assert.Equal("Admins only.", (await bot.PrivateChat().SendText("/stats")).Text);
    }

    [Fact]
    public async Task Global_filter_runs_for_actions_but_not_payments()
    {
        await using var bot = TestBot.Create(bot: b => b.AddFilter<CountingFilter>());
        var user = bot.PrivateChat();

        await user.SendText("/start");
        await user.SendCallback("buy");
        var payment = await user.PayInvoice();

        Assert.True(payment.Accepted);
        Assert.Equal("Premium is active", payment.Payment!.Text);
        Assert.Equal(["StartController.Start", "ShopController.Buy"], bot.Services.GetRequiredService<Counter>().Actions);
    }
}

public sealed class PaymentTests
{
    [Fact]
    public async Task Invoice_can_be_paid_once()
    {
        await using var bot = TestBot.Create();
        var user = bot.PrivateChat();

        var invoice = await user.SendCallback("buy");
        Assert.Equal(50, invoice.Of<SendInvoiceRequest>().Single().Prices.Single().Amount);

        var first = await user.PayInvoice(chargeId: "charge-1");
        var repeated = await user.PayInvoice(chargeId: "charge-1");

        Assert.True(first.Accepted);
        Assert.Equal("Premium is active", first.Payment!.Text);
        Assert.True(repeated.Payment!.IsEmpty);
        Assert.Equal(["charge-1"], bot.Services.GetRequiredService<PaymentLog>().Paid);
    }
}

public sealed class TestingHostTests
{
    [Fact]
    public async Task Immediate_ack_answers_every_button()
    {
        await using var bot = TestBot.Create(bot: b => b.AckCallbacks(CallbackAckMode.Immediate));
        var result = await bot.PrivateChat().SendCallback("catalog:2");
        Assert.Single(result.Of<AnswerCallbackQueryRequest>());
    }

    [Fact]
    public async Task Simulated_errors_reach_the_error_view()
    {
        await using var bot = TestBot.Create();
        var calls = 0;
        bot.Telegram.Handle<SendMessageRequest>(_ => ++calls == 1 ? throw new InvalidOperationException("boom") : null);

        // The action's answer fails to send; the dispatcher logs it and the bot keeps working.
        var failed = await bot.PrivateChat().SendText("/start");
        Assert.Single(failed.Of<SendMessageRequest>());
        Assert.Equal("Hello!", (await bot.PrivateChat().SendText("/start")).Text);
    }

    [Fact]
    public async Task Pressing_a_missing_button_explains_what_exists()
    {
        await using var bot = TestBot.Create();
        var user = bot.PrivateChat();
        await user.SendText("/menu");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => user.PressButton("Nope"));
        Assert.Contains("\"Catalog\"", error.Message);
    }
}
