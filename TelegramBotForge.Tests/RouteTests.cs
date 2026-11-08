using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.InlineQueryResults;
using TelegramBotForge.Controllers;
using TelegramBotForge.Hosting;
using TelegramBotForge.Media;
using TelegramBotForge.Results;
using TelegramBotForge.Testing;

namespace TelegramBotForge.Tests;

/// <summary>
/// A custom route: messages that contain #tag.
/// </summary>
public sealed class HashtagAttribute(string tag) : RouteAttribute
{
    public override int Priority => 25;

    public override string Key => "hashtag #" + tag;

    public override IReadOnlyList<string> ParameterNames => ["rest"];

    public override bool TryMatch(BotContext context, IDictionary<string, string> values)
    {
        if (context.Text is not { } text || !text.Contains("#" + tag, StringComparison.OrdinalIgnoreCase))
            return false;

        values["rest"] = text.Replace("#" + tag, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return true;
    }
}

public sealed class Members
{
    public List<string> Events { get; } = [];
}

public sealed class MoreRoutesController(Members members) : BotController
{
    [ButtonText("🛒 Catalog", "🛒 Каталог")]
    public IBotResult Catalog() => Reply("Catalog opened");

    [Regex(@"^(?<amount>\d+(\.\d+)?)\s*(?<currency>usd|eur)(\s+to\s+(?<target>usd|eur))?$", IgnoreCase = true)]
    public IBotResult Convert(decimal amount, string currency, string? target) =>
        Reply($"{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)} {currency.ToUpperInvariant()} -> {target ?? "default"}");

    [Hashtag("news")]
    public IBotResult News(string rest) => Reply($"News: {rest}");

    [Contact(OwnOnly = true)]
    public IBotResult OwnPhone(Contact contact) => Reply($"Saved {contact.PhoneNumber}");

    [Contact]
    public IBotResult OtherContact(Contact contact) => Reply("Please share your own number");

    [EditedText]
    public IBotResult Edited(string text, Message message) => Reply($"You changed message {message.MessageId} to {text}");

    [BotBlocked]
    public void Blocked(ChatMemberUpdated change) => members.Events.Add($"blocked by {change.From.Id}");

    [BotUnblocked]
    public void Unblocked() => members.Events.Add("unblocked");

    [AddedToChat]
    public IBotResult Added() => Reply("Thanks for adding me!");

    [RemovedFromChat]
    public void Removed() => members.Events.Add("removed from " + Context.ChatId);

    [InlineQuery]
    public IBotResult Search(string query, InlineQuery inline) => AnswerInline(
        Enumerable.Range(1, 3).Select(i => InlineResults.Article($"{query}-{i}", $"{query} #{i}", $"<b>{query}</b> {i}")),
        isPersonal: true,
        nextOffset: inline.Offset == "" ? "3" : null);

    [Command("services")]
    public IBotResult Services(Counter counter, [FromServices] Members members)
    {
        counter.Actions.Add("services");
        return Reply($"{counter.Actions.Count} {members.Events.Count}");
    }

    [Album]
    public IBotResult Album(IReadOnlyList<BotMedia> items) =>
        Reply($"Album of {items.Count}: {items.FirstOrDefault(item => item.Caption is not null)?.Caption ?? "no caption"}");
}

public sealed class MoreRouteTests
{
    private static BotTestHost Create() => TestBot.Create(services => services.AddSingleton<Members>());

    [Theory]
    [InlineData("🛒 Catalog")]
    [InlineData("  🛒 Каталог ")]
    public async Task Reply_keyboard_button_text_is_routed(string text)
    {
        await using var bot = Create();
        Assert.Equal("Catalog opened", (await bot.PrivateChat().SendText(text)).Text);
    }

    [Fact]
    public async Task Button_text_wins_over_a_state_action()
    {
        await using var bot = Create();
        var user = bot.PrivateChat();
        await user.SendText("/signup");
        Assert.Equal("Catalog opened", (await user.SendText("🛒 Catalog")).Text);
    }

    [Theory]
    [InlineData("12.5 usd", "12.5 USD -> default")]
    [InlineData("3 EUR to usd", "3 EUR -> usd")]
    public async Task Regex_groups_are_bound_to_parameters(string text, string expected)
    {
        await using var bot = Create();
        Assert.Equal(expected, (await bot.PrivateChat().SendText(text)).Text);
    }

    [Fact]
    public async Task Text_that_does_not_match_the_regex_is_unknown()
    {
        await using var bot = Create();
        Assert.Equal("Unknown command", (await bot.PrivateChat().SendText("12 gbp")).Text);
    }

    [Fact]
    public async Task Custom_route_attribute_works()
    {
        await using var bot = Create();
        Assert.Equal("News: big day", (await bot.PrivateChat().SendText("#news big day")).Text);
    }

    [Fact]
    public async Task Own_contact_and_someone_elses_contact_go_to_different_actions()
    {
        await using var bot = Create();
        var user = bot.PrivateChat();
        Assert.Equal("Saved +123", (await user.SendContact("+123")).Text);
        Assert.Equal("Please share your own number", (await user.SendContact("+456", ownContact: false)).Text);
    }

    [Fact]
    public async Task Edits_go_to_EditedText_and_never_rerun_commands()
    {
        await using var bot = Create();
        var user = bot.PrivateChat();
        await user.SendText("/start");
        var edited = await user.EditText("/start again");
        Assert.StartsWith("You changed message ", edited.Text);
        Assert.EndsWith(" to /start again", edited.Text);
    }

    [Fact]
    public async Task Blocking_and_unblocking_the_bot_are_routed()
    {
        await using var bot = Create();
        var user = bot.PrivateChat();
        Assert.True((await user.BlockBot()).IsEmpty);
        await user.UnblockBot();
        Assert.Equal([$"blocked by {user.User.Id}", "unblocked"], bot.Services.GetRequiredService<Members>().Events);
    }

    [Fact]
    public async Task Being_added_to_and_removed_from_a_group_is_routed()
    {
        await using var bot = Create();
        var group = bot.GroupChat(-100500);
        var added = await group.AddBot();
        Assert.Equal("Thanks for adding me!", added.Text);
        Assert.Equal(-100500, added.Of<SendMessageRequest>().Single().ChatId.Identifier);

        await group.RemoveBot();
        Assert.Equal(["removed from -100500"], bot.Services.GetRequiredService<Members>().Events);
    }

    [Fact]
    public async Task Inline_query_is_answered()
    {
        await using var bot = Create();
        var answer = (await bot.PrivateChat().SendInlineQuery("pizza")).InlineAnswer!;

        Assert.Equal(3, answer.Results.Count());
        var first = Assert.IsType<InlineQueryResultArticle>(answer.Results.First());
        Assert.Equal("pizza #1", first.Title);
        Assert.Equal("<b>pizza</b> 1", ((InputTextMessageContent)first.InputMessageContent).MessageText);
        Assert.True(answer.IsPersonal);
        Assert.Equal("3", answer.NextOffset);

        var next = (await bot.PrivateChat().SendInlineQuery("pizza", offset: "3")).InlineAnswer!;
        Assert.Null(next.NextOffset);
    }

    [Fact]
    public async Task Album_reaches_the_action_once_with_all_items()
    {
        await using var bot = Create();
        var result = await bot.PrivateChat().SendAlbum(4, caption: "holiday");
        Assert.Equal("Album of 4: holiday", result.Text);
    }

    [Fact]
    public async Task Queue_collects_album_items_and_keeps_the_order()
    {
        await using var bot = Create();
        var queues = bot.Services.GetRequiredService<UpdateQueues>();
        await queues.StartAsync(CancellationToken.None);

        var user = new User { Id = 777, FirstName = "A" };
        var chat = new Chat { Id = 777, Type = Telegram.Bot.Types.Enums.ChatType.Private };
        var id = 0;
        Update Photo(string group, string? caption = null) => new()
        {
            Id = ++id,
            Message = new Message
            {
                Id = id, Date = DateTime.UtcNow, Chat = chat, From = user, MediaGroupId = group, Caption = caption,
                Photo = [new PhotoSize { FileId = "f" + id, FileUniqueId = "u" + id, Width = 1, Height = 1 }]
            }
        };

        await queues.EnqueueAsync(Photo("a", "first"), CancellationToken.None);
        await queues.EnqueueAsync(Photo("a"), CancellationToken.None);
        await queues.EnqueueAsync(Photo("a"), CancellationToken.None);
        await queues.EnqueueAsync(Photo("b", "second"), CancellationToken.None);
        await queues.EnqueueAsync(Photo("b"), CancellationToken.None);
        await queues.EnqueueAsync(new Update
        {
            Id = ++id,
            Message = new Message { Id = id, Date = DateTime.UtcNow, Chat = chat, From = user, Text = "/echo after" }
        }, CancellationToken.None);
        await queues.EnqueueAsync(Photo("c", "late"), CancellationToken.None);
        await queues.EnqueueAsync(Photo("c"), CancellationToken.None);

        await queues.StopAsync(CancellationToken.None);

        var texts = bot.Telegram.RequestsOf<SendMessageRequest>().Select(request => request.Text).ToList();
        Assert.Equal(["Album of 3: first", "Album of 2: second", "after", "Album of 2: late"], texts);
    }

    [Fact]
    public async Task Actions_take_registered_services_as_parameters()
    {
        await using var bot = Create();
        Assert.Equal("1 0", (await bot.PrivateChat().SendText("/services")).Text);
    }

    [Fact]
    public void Unregistered_parameter_type_stops_the_bot_at_startup()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Routing.ActionRegistry.Build([], [typeof(UnknownServiceController)]));
        Assert.Contains("'members' (Members) has no source. If it is a service, register it", error.Message);
    }

    [Fact]
    public void Invalid_regex_stops_the_bot_at_startup()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Routing.ActionRegistry.Build([], [typeof(BadRegexController)]));
        Assert.Contains("BadRegexController.Bad", error.Message);
    }

    [Fact]
    public void Regex_group_without_a_parameter_stops_the_bot_at_startup()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Routing.ActionRegistry.Build([], [typeof(UnboundGroupController)]));
        Assert.Contains("{id} has no parameter", error.Message);
    }
}

// Abstract, so the test bot does not pick these up; the registry still reads their routes.
public abstract class BadRegexController : BotController
{
    [Regex("(unclosed")]
    public IBotResult Bad() => Reply("x");
}

public abstract class UnknownServiceController : BotController
{
    [Command("x")]
    public IBotResult X(Members members) => Reply("x");
}

public abstract class UnboundGroupController : BotController
{
    [Regex(@"^order (?<id>\d+)$")]
    public IBotResult Order() => Reply("x");
}
