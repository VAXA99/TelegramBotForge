using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Requests;
using TelegramBotForge.Controllers;
using TelegramBotForge.DeepLinks;
using TelegramBotForge.Hosting;
using TelegramBotForge.Results;
using TelegramBotForge.Routing;

namespace TelegramBotForge.Tests;

public sealed class DeepLinkController : BotController
{
    [DeepLink("ref{userId:long}")]
    public IBotResult Referral(long userId) => Reply($"Referral {userId}");

    [DeepLink("short{id}")]
    [Callback("short:{id}")]
    public IBotResult Short(Guid id) => Reply(id.ToString());
}

public sealed class DeepLinkTests
{
    [Fact]
    public async Task Builds_private_and_group_links_using_automatic_identity()
    {
        await using var bot = TestBot.Create();
        bot.Services.GetRequiredService<BotIdentity>().Username = null;
        var links = bot.Services.GetRequiredService<IBotDeepLinks>();
        Assert.Equal("https://t.me/test_bot?start=ref42", await links.CreateAsync("ref42"));
        Assert.Equal("https://t.me/test_bot?startgroup=group_42-", await links.CreateAsync("group_42-", group: true));
        Assert.Single(bot.Telegram.RequestsOf<GetMeRequest>());
        Assert.EndsWith("start=" + new string('a', 64), await links.CreateAsync(new string('a', 64)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("colon:value")]
    [InlineData("with\nnewline")]
    [InlineData("unicode_ж")]
    public async Task Invalid_payload_throws_synchronously_before_any_network_request(string payload)
    {
        await using var bot = TestBot.Create();
        var links = bot.Services.GetRequiredService<IBotDeepLinks>();
        Assert.Throws<ArgumentException>(() => { _ = links.CreateAsync(payload); });
        Assert.Throws<ArgumentException>(() => { _ = links.CreateAsync(new string('a', 65)); });
        Assert.Throws<ArgumentNullException>(() => { _ = links.CreateAsync(null!); });
        Assert.Empty(bot.Telegram.Requests);
    }

    [Fact]
    public void Short_guids_round_trip_and_fit_two_ids_in_callback_data()
    {
        foreach (var value in new[] { Guid.Empty, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Guid.NewGuid() })
        {
            var encoded = ShortId.Encode(value);
            Assert.Equal(22, encoded.Length);
            Assert.Matches("^[A-Za-z0-9_-]{22}$", encoded);
            Assert.Equal(value, ShortId.Decode(encoded));
        }
        var id = ShortId.Encode(Guid.NewGuid());
        Assert.Equal(55, System.Text.Encoding.UTF8.GetByteCount($"cart:{id}:item:{id}"));
        Assert.False(ShortId.TryDecode(null, out _));
        Assert.False(ShortId.TryDecode(new string('a', 21), out _));
        Assert.False(ShortId.TryDecode(new string('a', 21) + "B", out _));
        Assert.Throws<FormatException>(() => ShortId.Decode("invalid"));
    }

    [Fact]
    public async Task Typed_deep_links_take_precedence_over_start_and_keep_its_fallback()
    {
        await using var bot = TestBot.Create();
        var user = bot.PrivateChat();
        Assert.Equal("Referral 9223372036854775807", (await user.SendText("/start ref9223372036854775807")).Text);
        Assert.Equal("Referral -42", (await user.SendText("/start ref-42")).Text);
        Assert.Equal("Invited by ref9223372036854775808", (await user.SendText("/start ref9223372036854775808")).Text);
        Assert.Equal("Invited by unknown", (await user.SendText("/start unknown")).Text);
        Assert.Equal("Hello!", (await user.SendText("/start")).Text);
        Assert.Equal("Referral 42", (await bot.GroupChat().SendText("/start@test_bot ref42")).Text);
        var ignored = await bot.GroupChat().SendText("/start@other_bot ref42");
        Assert.Empty(ignored.Requests.OfType<SendMessageRequest>());
        var id = Guid.NewGuid();
        Assert.Equal(id.ToString(), (await user.SendText("/start short" + ShortId.Encode(id))).Text);
        Assert.Equal(id.ToString(), (await user.SendCallback("short:" + ShortId.Encode(id))).Text);
    }

    [Theory]
    [InlineData("ref:{id}")]
    [InlineData("ref{id:float}")]
    [InlineData("ref{id}{id}")]
    [InlineData("ref{broken")]
    [InlineData(" ")]
    public void Invalid_templates_are_rejected(string template) =>
        Assert.ThrowsAny<ArgumentException>(() => new DeepLinkAttribute(template));

    [Fact]
    public void Impossible_template_length_and_bad_parameter_binding_fail_at_startup()
    {
        Assert.Throws<ArgumentException>(() => new DeepLinkAttribute(new string('a', 64) + "{id}"));
        var malformed = Assert.Throws<InvalidOperationException>(() => ActionRegistry.Build([], [typeof(BadTemplateController)]));
        Assert.Contains("BadTemplateController", malformed.Message);
        var unbound = Assert.Throws<InvalidOperationException>(() => ActionRegistry.Build([], [typeof(UnboundController)]));
        Assert.Contains("{id} has no parameter", unbound.Message);
        var duplicate = Assert.Throws<InvalidOperationException>(() => ActionRegistry.Build([], [typeof(DuplicateController<int>)]));
        Assert.Contains("same route", duplicate.Message);
    }

    private abstract class BadTemplateController : BotController
    {
        [DeepLink("bad:{id}")] public IBotResult Bad(int id) => Reply("bad");
    }
    private abstract class UnboundController : BotController
    {
        [DeepLink("unbound{id}")] public IBotResult Bad() => Reply("bad");
    }
    private sealed class DuplicateController<T> : BotController
    {
        public DuplicateController() { }
        [DeepLink("duplicate{id}")] public IBotResult First(int id) => Reply("one");
        [DeepLink("duplicate{id}")] public IBotResult Second(int id) => Reply("two");
    }
}
