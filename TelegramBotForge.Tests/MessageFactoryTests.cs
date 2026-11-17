using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Controllers;
using TelegramBotForge.Media;
using TelegramBotForge.Results;
using TelegramBotForge.Testing;
using TelegramBotForge.Views;
using Message = TelegramBotForge.Views.Message;

namespace TelegramBotForge.Tests;

public sealed class FactoryView(Counter counter) : View
{
    public Message Home(string name = "Guest") => Message().Heading(name).Paragraph("Welcome");
    [Rich] public Message RichScreen() => Message().Paragraph("Rich");
    public Message Count() => Message().Paragraph(counter.Actions.Count.ToString());
    public Message InvalidHelper() => RootHelper();
    private Message RootHelper() => Message().Paragraph("helper");
    [Standard] public Message Notice(string html) => Message().RawHtml(html);
}

[View]
public sealed class PlainFactoryView(Counter counter)
{
    public Message Screen() => Message.Create<PlainFactoryView>().Paragraph(counter.Actions.Count.ToString());
}

[View(Lifetime = ServiceLifetime.Singleton, Rich = true)]
public abstract class SingletonFactoryBase : View;
public sealed class SingletonFactoryView : SingletonFactoryBase
{
    public Message Screen() => Message().Paragraph("Singleton");
}

[View(Rich = true)]
public abstract class RichFactoryBase : View
{
    public virtual Message Inherited() => Message().Heading("Inherited");
    [Standard] public virtual Message Override() => Message().Paragraph("Standard");
}

public sealed class RichFactoryView : RichFactoryBase
{
    [Standard] public Message StandardScreen() => Message().Paragraph("Standard");
    public override Message Override() => Message().Paragraph("Inherited standard");
}

public sealed class OverrideFactoryView : RichFactoryBase
{
    [Rich] public override Message Override() => Message().Paragraph("Explicit rich");
}

public sealed class FactoryController(FactoryView view) : BotController
{
    [Command("factory")] public IBotResult Start() => Reply(view.Home("Factory"));
    [Callback("factory:edit")] public IBotResult Change() => Edit(view.Home("Changed"));
}

public sealed class MessageFactoryTests
{
    private abstract class HiddenBase : View
    {
        public Message Screen() => Message().Paragraph("base");
    }
    private abstract class HiddenScreen : HiddenBase
    {
        public new Message Screen() => Message().Paragraph("hidden");
    }
    private abstract class ConflictingBase : View
    {
        [Rich, Standard] public virtual Message Screen() => Message().Paragraph("conflict");
    }
    private abstract class ConflictingOverride : ConflictingBase
    {
        [Standard] public override Message Screen() => Message().Paragraph("override");
    }

    [Fact]
    public void Hidden_methods_and_conflicting_ancestor_annotations_are_rejected()
    {
        Assert.Contains("ambiguous", Assert.Throws<InvalidOperationException>(() => ViewMetadata.Validate(typeof(HiddenScreen))).Message);
        Assert.Contains("conflicting", Assert.Throws<InvalidOperationException>(() => ViewMetadata.Validate(typeof(ConflictingOverride))).Message);
    }

    [Fact]
    public async Task Failed_replacement_rolls_back_new_media_and_keeps_previous_output()
    {
        var (context, bot) = Context();
        await Deliver(context, new DeliverMessage(Standard()));
        var previous = context.Deliveries.Last();
        bot.ClearRequests();
        bot.Handle<SendMessageRequest>(_ => throw new ApiRequestException("Bad Request: can't parse entities", 400));
        await Assert.ThrowsAsync<ApiRequestException>(() => Deliver(context,
            new DeliverMessage(Standard().Photo(Photo), Edit: true, Previous: previous)));
        Assert.Equal(previous.PrimaryMessageId, Assert.Single(bot.MessagesIn(42)).Id);
        Assert.Single(bot.RequestsOf<SendPhotoRequest>());
        Assert.Single(bot.RequestsOf<DeleteMessageRequest>());
        Assert.Single(context.Deliveries);
    }

    [Fact]
    public async Task Cleanup_failure_still_reports_the_actual_new_delivery()
    {
        var (context, bot) = Context();
        await Deliver(context, new DeliverMessage(Standard().Photo(Photo)));
        var previous = context.Deliveries.Last();
        MessageDelivery? observed = null;
        bot.Handle<DeleteMessageRequest>(_ => throw new ApiRequestException("Forbidden", 403));
        await Assert.ThrowsAsync<MessageDeliveryException>(() => Deliver(context, new DeliverMessage(
            Rich().OnDelivered((receipt, _) => { observed = receipt; return Task.CompletedTask; }), Edit: true, Previous: previous)));
        Assert.Same(context.Deliveries.Last(), observed);
        Assert.Equal(2, bot.RequestsOf<DeleteMessageRequest>().Count);
    }

    [Fact]
    public async Task Restored_receipts_replace_associated_media_and_clean_up_previous_IDs()
    {
        var (context, bot) = Context();
        var screen = Standard().Photo(Photo);
        await Deliver(context, new DeliverMessage(screen));
        var original = context.Deliveries.Last();
        var restored = JsonSerializer.Deserialize<MessageDelivery>(JsonSerializer.Serialize(original))!;
        await Deliver(context, new DeliverMessage(screen, Edit: true, Previous: restored));
        Assert.Equal(2, bot.MessagesIn(42).Count);
        Assert.NotEqual(original.PrimaryMessageId, context.Deliveries.Last().PrimaryMessageId);
        Assert.DoesNotContain(bot.MessagesIn(42), message => original.AssociatedMessageIds.Contains(message.Id));
    }

    [Fact]
    public async Task Legacy_edits_do_not_replace_unrelated_API_errors()
    {
        var (context, bot) = Context();
        bot.Handle<EditMessageTextRequest>(_ => throw new ApiRequestException("Bad Request: can't parse entities", 400));
        await Assert.ThrowsAsync<ApiRequestException>(() => new TelegramResultExecutor().ExecuteAsync(context,
            new EditText("invalid", MessageId: 999), CancellationToken.None));
        Assert.Empty(bot.RequestsOf<SendMessageRequest>());
    }

    [Fact]
    public void Contact_and_location_requests_share_existing_keyboard_checks()
    {
        var keyboard = Assert.IsType<ReplyKeyboardMarkup>(Evaluate(Standard().RequestContact("Contact").RequestLocation("Location")).Message.Keyboard);
        Assert.True(keyboard.Keyboard.First().Single().RequestContact);
        Assert.True(keyboard.Keyboard.Last().Single().RequestLocation);
        Assert.Throws<InvalidOperationException>(() => Evaluate(Rich().RequestContact("Unsupported")));
        Assert.Throws<InvalidOperationException>(() => Evaluate(Standard().RequestContact("Mixed").Row(Buttons.Callback("Inline"))));
    }
    private static readonly MediaFile Photo = MediaFile.FromFileId(MediaType.Photo, "photo-id");
    private static readonly MediaFile Video = MediaFile.FromFileId(MediaType.Video, "video-id");
    private static Message Standard() => new FactoryView(new Counter()).Home();
    private static Message Rich() => new FactoryView(new Counter()).RichScreen();
    private static MessagePayload Evaluate(Message message) => MessageEvaluation.Evaluate(message);

    [Fact]
    public async Task Discovery_injection_scopes_and_controller_helpers_work()
    {
        await using var bot = TestBot.Create();
        using var firstScope = bot.Services.CreateScope();
        using var secondScope = bot.Services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<FactoryView>();
        Assert.Same(first, firstScope.ServiceProvider.GetRequiredService<FactoryView>());
        Assert.NotSame(first, secondScope.ServiceProvider.GetRequiredService<FactoryView>());
        bot.Services.GetRequiredService<Counter>().Actions.Add("injected");
        Assert.Contains("1", Evaluate(first.Count()).Message.Text);
        Assert.False(Evaluate(firstScope.ServiceProvider.GetRequiredService<PlainFactoryView>().Screen()).Message.Rich);
        Assert.Same(firstScope.ServiceProvider.GetRequiredService<SingletonFactoryView>(),
            secondScope.ServiceProvider.GetRequiredService<SingletonFactoryView>());
        var chat = bot.PrivateChat();
        await chat.SendText("/factory");
        Assert.Contains("Factory", bot.Telegram.RequestsOf<SendMessageRequest>().Last().Text);
    }

    [Fact]
    public void Default_view_method_and_inherited_modes_are_deterministic()
    {
        Assert.False(Evaluate(Standard()).Message.Rich);
        Assert.True(Evaluate(Rich()).Message.Rich);
        var view = new RichFactoryView();
        Assert.True(Evaluate(view.Inherited()).Message.Rich);
        Assert.False(Evaluate(view.StandardScreen()).Message.Rich);
        Assert.False(Evaluate(view.Override()).Message.Rich);
        Assert.True(Evaluate(new OverrideFactoryView().Override()).Message.Rich);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Startup_rejects_conflicts_and_overloads(bool conflicting)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Views").DefineType("InvalidView", TypeAttributes.Public, typeof(View));
        var method = type.DefineMethod("Screen", MethodAttributes.Public, typeof(Message), Type.EmptyTypes);
        method.GetILGenerator().Emit(OpCodes.Ldnull);
        method.GetILGenerator().Emit(OpCodes.Ret);
        if (conflicting)
        {
            method.SetCustomAttribute(new CustomAttributeBuilder(typeof(RichAttribute).GetConstructor(Type.EmptyTypes)!, []));
            method.SetCustomAttribute(new CustomAttributeBuilder(typeof(StandardAttribute).GetConstructor(Type.EmptyTypes)!, []));
        }
        else
        {
            var overload = type.DefineMethod("Screen", MethodAttributes.Public, typeof(Message), [typeof(string)]);
            overload.GetILGenerator().Emit(OpCodes.Ldnull);
            overload.GetILGenerator().Emit(OpCodes.Ret);
        }
        type.CreateType();
        var exception = Assert.Throws<InvalidOperationException>(() => ViewRegistration.AddViews(new ServiceCollection(), assembly));
        Assert.Contains("InvalidView.Screen", exception.Message);
        Assert.Contains(conflicting ? "conflicting" : "ambiguous", exception.Message);
    }

    [Fact]
    public void Text_is_escaped_raw_is_explicit_and_formatting_is_nested()
    {
        var html = Evaluate(Standard().Paragraph("<b>& user</b>")
            .Paragraph(Text.Bold(Text.Italic("<&>")))
            .RawHtml("<i>trusted</i>")
            .Paragraph(Text.Link("<link>", "https://example.com/?a=1&b=2"))).Message.Text;
        Assert.Contains("&lt;b&gt;&amp; user&lt;/b&gt;", html);
        Assert.Contains("<b><i>&lt;&amp;&gt;</i></b>", html);
        Assert.Contains("<i>trusted</i>", html);
        Assert.Contains("a=1&amp;b=2", html);
        Assert.Contains("&lt;link&gt;</a>", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tables_details_inline_and_components_preserve_structure(bool rich)
    {
        var message = (rich ? Rich() : Standard())
            .Component(body => body.Heading(Text.Concat("Name ", Text.Emoji("123", "*")))
                .Table(table => table.Field("Age", "30").Row("a", "b", "c"))
                .Details("Preferences", section => section.Component(nested => nested.Paragraph(Text.Underline("kind")))))
            .Paragraph(Text.Join(", ", new Text[] { Text.Strike("old"), Text.Code("new") }));
        var html = Evaluate(message).Message.Text;
        Assert.Contains("<tg-emoji emoji-id=\"123\">*</tg-emoji>", html);
        Assert.Contains("<u>kind</u>", html);
        Assert.Contains("<s>old</s>, <code>new</code>", html);
        if (rich)
        {
            Assert.Contains("<table compact><tr><td>Age</td><td>30</td></tr>", html);
            Assert.Contains("<details><summary>Preferences</summary>", html);
        }
        else
        {
            Assert.Contains("<b>Age</b>: 30", html);
            Assert.Contains("a | b | c", html);
            Assert.Contains("<b>Preferences</b>\n", html);
            Assert.DoesNotContain("<details>", html);
        }
    }

    [Fact]
    public void Rich_buttons_stay_between_content_and_standard_buttons_move_to_keyboard()
    {
        var button = Buttons.Callback("Fallback", "next").Label(Text.Concat("Next ", "<"))
            .Style(ButtonStyle.Primary).Icon("123", "*");
        var rich = Evaluate(Rich().Paragraph("before").Row(button).Paragraph("after")).Message;
        Assert.True(rich.Text.IndexOf("before", StringComparison.Ordinal) < rich.Text.IndexOf("<tg-button-row>", StringComparison.Ordinal));
        Assert.True(rich.Text.IndexOf("</tg-button-row>", StringComparison.Ordinal) < rich.Text.IndexOf("after", StringComparison.Ordinal));
        Assert.Contains("style=\"primary\"", rich.Text);
        Assert.Contains("Next &lt;", rich.Text);
        Assert.Null(rich.Keyboard);
        var standard = Evaluate(Standard().Row(button).Paragraph("after").Row(Buttons.Url("URL", "https://example.com"), Buttons.Copy("Copy", "text"))).Message;
        var rows = Assert.IsType<InlineKeyboardMarkup>(standard.Keyboard).InlineKeyboard.ToArray();
        Assert.Equal("Next <", rows[0].Single().Text);
        Assert.Equal("next", rows[0].Single().CallbackData);
        Assert.Equal("text", rows[1].Last().CopyText!.Text);
    }

    [Fact]
    public void Restrictions_name_the_view_and_screen()
    {
        var invalid = new[]
        {
            Standard().Row(Buttons.Callback("badge").Disabled()),
            Rich().Row(Buttons.Callback("bold").Label(Text.Bold("unsupported"))),
            Standard().Row(Buttons.Callback("code").Label(Text.Code("bad"))),
            Rich().ReplyButtons(["bad"]),
            Standard().ReplyButtons(["bad"]).Row(Buttons.Callback("mixed")),
            Standard().Paragraph(new string('x', 4097)),
            Standard().RawHtml("<details>rich only</details>"),
            Standard().RawHtml("<b>unclosed"),
            Rich().RawHtml("<script>bad</script>"),
            Rich().RawHtml("<img src=\"tg://photo?id=missing\"/>"),
            Rich().Slideshow([MediaFile.FromFileId(MediaType.Document, "bad")]),
            Rich().Slideshow(Enumerable.Repeat(Photo, 51)),
            Standard().Row(Enumerable.Repeat(Buttons.Callback("too many"), 9).ToArray())
        };
        foreach (var message in invalid)
        {
            var exception = Assert.Throws<InvalidOperationException>(() => Evaluate(message));
            Assert.Contains("FactoryView.", exception.Message);
        }
        Assert.Throws<ArgumentException>(() => Buttons.Callback("large", new string('a', 65)));
    }

    [Fact]
    public void Messages_copy_collections_and_components_at_declaration_time()
    {
        var buttons = new[] { Buttons.Callback("Original") };
        var media = new[] { Photo };
        var original = Standard();
        var child = original.Row(buttons).Slideshow(media);
        buttons[0] = Buttons.Callback("Changed");
        media[0] = Video;
        _ = child.Paragraph("Later");
        Assert.Null(Evaluate(original).Message.Keyboard);
        var payload = Evaluate(child);
        Assert.Equal("Original", Assert.IsType<InlineKeyboardMarkup>(payload.Message.Keyboard).InlineKeyboard.Single().Single().Text);
        Assert.Same(Photo, payload.Attachments.Single());
        Assert.DoesNotContain("Later", payload.Message.Text);
        Assert.Throws<InvalidOperationException>(() => original.Component(_ => Rich()));
        Assert.Throws<InvalidOperationException>(() => original.Component(_ => Standard()));
        Assert.Throws<InvalidOperationException>(() => original.Component(body => body.OnDelivered((_, _) => Task.CompletedTask)));
    }

    [Fact]
    public void Pagination_reuses_existing_page_window_and_defaults()
    {
        var message = Standard().Page(new[] { "a", "b", "c" }, 9, 2,
            (body, items, info) => body.Paragraph($"{info.Page}:{string.Join(",", items)}"), page => $"p:{page}");
        var result = Evaluate(message).Message;
        Assert.Contains("2:c", result.Text);
        Assert.Equal("p:1", Assert.IsType<InlineKeyboardMarkup>(result.Keyboard).InlineKeyboard.Single().First().CallbackData);
    }

    private static (BotContext Context, FakeTelegramBotClient Bot) Context()
    {
        var bot = new FakeTelegramBotClient();
        var context = new BotContext(new Update
        {
            Message = new Telegram.Bot.Types.Message { Id = 1, Chat = new Chat { Id = 42 } }
        }, bot, new ServiceCollection().BuildServiceProvider(), CancellationToken.None);
        return (context, bot);
    }
    private static Task Deliver(BotContext context, DeliverMessage result) => new TelegramResultExecutor().ExecuteAsync(context, result, CancellationToken.None);

    [Fact]
    public async Task Rich_media_is_registered_and_actual_primary_id_is_reported()
    {
        var (context, bot) = Context();
        MessageDelivery? observed = null;
        await Deliver(context, new DeliverMessage(Rich().Component(body => body.Photo(Photo).Video(Video))
            .Row(Buttons.Callback("Next", "next"))
            .OnDelivered((receipt, _) => { observed = receipt; return Task.CompletedTask; })));
        var request = Assert.Single(bot.RequestsOf<SendRichMessageRequest>());
        Assert.Equal(new[] { "m0", "m1" }, request.RichMessage.Media!.Select(item => item.Id));
        Assert.IsType<InputMediaPhoto>(request.RichMessage.Media!.First().Media);
        Assert.IsType<InputMediaVideo>(request.RichMessage.Media!.Last().Media);
        Assert.Contains("tg://photo?id=m0", request.RichMessage.Html);
        Assert.Contains("tg://video?id=m1", request.RichMessage.Html);
        Assert.NotNull(observed);
        Assert.Equal(Assert.Single(bot.MessagesIn(42)).Id, observed.PrimaryMessageId);
        Assert.Empty(observed.AssociatedMessageIds);
        Assert.Same(observed, Assert.Single(context.Deliveries));
    }

    [Fact]
    public async Task Standard_media_controls_and_ids_are_managed_across_edits()
    {
        var (context, bot) = Context();
        var screen = Standard().Slideshow([Photo, Video]).Row(Buttons.Callback("Next", "next"));
        await Deliver(context, new DeliverMessage(screen));
        var first = context.Deliveries.Last();
        Assert.Equal(2, first.AssociatedMessageIds.Count);
        Assert.Equal(bot.MessagesIn(42).Last().Id, first.PrimaryMessageId);
        Assert.Equal(first.AssociatedMessageIds, bot.MessagesIn(42).Take(2).Select(message => message.Id));
        Assert.NotNull(bot.MessagesIn(42).Last().ReplyMarkup);
        bot.ClearRequests();
        await Deliver(context, new DeliverMessage(screen.Paragraph("Updated"), Edit: true, Previous: first));
        Assert.Single(bot.RequestsOf<EditMessageTextRequest>());
        Assert.Empty(bot.RequestsOf<SendPhotoRequest>());
        Assert.Equal(first.PrimaryMessageId, context.Deliveries.Last().PrimaryMessageId);
        await Deliver(context, new DeliverMessage(Standard().Photo(Photo), Edit: true, Previous: context.Deliveries.Last()));
        Assert.Equal(2, bot.MessagesIn(42).Count);
        Assert.Equal(context.Deliveries.Last().PrimaryMessageId, bot.MessagesIn(42).Last().Id);
        Assert.All(first.AssociatedMessageIds, old => Assert.DoesNotContain(bot.MessagesIn(42), message => message.Id == old));
    }

    [Fact]
    public async Task Missing_primary_is_replaced_and_unchanged_edits_keep_the_id()
    {
        var (context, bot) = Context();
        await Deliver(context, new DeliverMessage(Standard(), Edit: true, MessageId: 999));
        var first = context.Deliveries.Last();
        bot.Handle<EditMessageTextRequest>(_ => throw new ApiRequestException("Bad Request: message is not modified", 400));
        await Deliver(context, new DeliverMessage(Standard(), Edit: true, Previous: first));
        Assert.Single(bot.MessagesIn(42));
        Assert.Equal(first.PrimaryMessageId, context.Deliveries.Last().PrimaryMessageId);
    }

    [Fact]
    public async Task Invalid_API_edits_are_not_retried()
    {
        var (context, bot) = Context();
        await Deliver(context, new DeliverMessage(Standard()));
        var first = context.Deliveries.Last();
        bot.ClearRequests();
        bot.Handle<EditMessageTextRequest>(_ => throw new ApiRequestException("Bad Request: can't parse entities", 400));
        var exception = await Assert.ThrowsAsync<ApiRequestException>(() => Deliver(context,
            new DeliverMessage(Standard().Paragraph("Updated"), Edit: true, Previous: first)));
        Assert.Contains("FactoryView.Home", exception.Data["TelegramBotForge.View"]!.ToString());
        Assert.Empty(bot.RequestsOf<SendMessageRequest>());
        Assert.Empty(bot.RequestsOf<DeleteMessageRequest>());
        Assert.Single(bot.MessagesIn(42));
        Assert.Single(context.Deliveries);
    }

    [Fact]
    public async Task Mode_changes_replace_once_and_remove_all_previous_output()
    {
        var (context, bot) = Context();
        await Deliver(context, new DeliverMessage(Standard().Photo(Photo)));
        var previous = context.Deliveries.Last();
        await Deliver(context, new DeliverMessage(Rich().Photo(Photo), Edit: true, Previous: previous));
        Assert.Single(bot.MessagesIn(42));
        Assert.Single(bot.RequestsOf<SendRichMessageRequest>());
        Assert.Equal(2, bot.RequestsOf<DeleteMessageRequest>().Count);
    }

    [Fact]
    public void Legacy_interpolation_and_raw_insertion_are_unchanged()
    {
        var builder = new MessageBuilder("Legacy", false, _ => { });
        var unsafeText = "<name>";
        var message = builder.Line($"<b>{unsafeText}</b>").Line(new RawHtml("<i>raw</i>")).Build();
        Assert.Equal("<b>&lt;name&gt;</b>\n<i>raw</i>", message.Text);
    }

    [Fact]
    public void Helpers_append_to_a_parent_and_new_views_do_not_require_legacy_overrides()
    {
        var view = new FactoryView(new Counter());
        Assert.Throws<NotSupportedException>(() => view.Render());
        var error = Assert.Throws<InvalidOperationException>(() => view.InvalidHelper());
        Assert.Contains("RootHelper", error.Message);
        Assert.Equal("<b>Raw only</b>", Evaluate(view.Notice("<b>Raw only</b>")).Message.Text);
    }

    [Fact]
    public void Raw_fragments_are_validated_as_a_complete_payload()
    {
        var message = Standard().RawHtml("<b>").Paragraph("<&>").RawHtml("</b>");
        Assert.Contains("&lt;&amp;&gt;", Evaluate(message).Message.Text);
        Assert.Contains("<details open>", Evaluate(Rich().RawHtml("<details open><summary>More</summary><p>Body</p></details>")).Message.Text);
        Assert.Contains("tg://photo?id=literal", Evaluate(Standard().Paragraph("tg://photo?id=literal")).Message.Text);
        Assert.Throws<InvalidOperationException>(() => Evaluate(Rich().Photo(Photo).RawHtml("<video src=\"tg://photo?id=m0\"/>")));
        Assert.Throws<InvalidOperationException>(() => Evaluate(Rich().RawHtml("<tg-button type=\"callback_data\" data=\"\">Empty</tg-button>")));
    }

    [Fact]
    public void Rich_URL_copy_and_disabled_actions_use_existing_factories()
    {
        var html = Evaluate(Rich().Row(Buttons.Url("Open", "https://example.com"), Buttons.Copy("Copy", "<&>"),
            Buttons.Callback("Badge").Disabled().Style(ButtonStyle.Success))).Message.Text;
        Assert.Contains("type=\"url\" url=\"https://example.com\"", html);
        Assert.Contains("type=\"copy_text\" text=\"&lt;&amp;&gt;\"", html);
        Assert.Contains("type=\"disabled\" style=\"success\"", html);
    }

    [Fact]
    public void Rich_limits_and_inline_restrictions_are_checked_before_delivery()
    {
        Assert.Contains("32768", Assert.Throws<InvalidOperationException>(() => Evaluate(Rich().Paragraph(new string('a', 32769)))).Message);
        Assert.Contains("20 columns", Assert.Throws<InvalidOperationException>(() => Evaluate(Rich()
            .Table(table => table.Row(Enumerable.Repeat(Text.Plain("cell"), 21).ToArray())))).Message);
        var tooManyBlocks = Rich();
        for (var count = 0; count < 501; count++)
            tooManyBlocks = tooManyBlocks.Paragraph("block");
        Assert.Contains("500 block", Assert.Throws<InvalidOperationException>(() => Evaluate(tooManyBlocks)).Message);
        Assert.Contains("16 level", Assert.Throws<InvalidOperationException>(() => Evaluate(Rich().RawHtml(
            string.Concat(Enumerable.Repeat("<details><summary>s</summary>", 17)) + "<p>body</p>" +
            string.Concat(Enumerable.Repeat("</details>", 17))))).Message);
        Assert.Throws<InvalidOperationException>(() => Evaluate(Standard().Paragraph(Text.Bold(Text.Code("invalid")))));
        Assert.Contains("<pre>&lt;code&gt;</pre>", Evaluate(Rich().Paragraph(Text.Preformatted("<code>"))).Message.Text);
    }

    [Fact]
    public async Task Immutable_descriptions_are_safe_to_deliver_concurrently()
    {
        var screen = Rich().Photo(Photo).Row(Buttons.Callback("Next", "next"));
        var contexts = Enumerable.Range(0, 8).Select(_ => Context()).ToArray();
        await Task.WhenAll(contexts.Select(pair => Deliver(pair.Context, new DeliverMessage(screen))));
        Assert.All(contexts, pair =>
        {
            Assert.Single(pair.Bot.RequestsOf<SendRichMessageRequest>());
            Assert.Single(pair.Context.Deliveries);
        });
    }

    [Fact]
    public async Task Failed_media_delivery_removes_partial_output()
    {
        var (context, bot) = Context();
        bot.Handle<SendVideoRequest>(_ => throw new ApiRequestException("Bad Request: wrong file identifier", 400));
        await Assert.ThrowsAsync<ApiRequestException>(() => Deliver(context, new DeliverMessage(Standard().Slideshow([Photo, Video]))));
        Assert.Empty(bot.MessagesIn(42));
        Assert.Empty(context.Deliveries);
        Assert.Single(bot.RequestsOf<DeleteMessageRequest>());
    }

    [Fact]
    public async Task Missing_primary_with_media_reuses_associated_output_without_duplication()
    {
        var (context, bot) = Context();
        var message = Standard().Photo(Photo);
        await Deliver(context, new DeliverMessage(message));
        var previous = context.Deliveries.Last();
        await new TelegramResultExecutor().ExecuteAsync(context, new DeleteMessage(previous.PrimaryMessageId), CancellationToken.None);
        await Deliver(context, new DeliverMessage(message, Edit: true, Previous: previous));
        Assert.Equal(2, bot.MessagesIn(42).Count);
        Assert.Single(bot.RequestsOf<SendPhotoRequest>());
        Assert.NotEqual(previous.PrimaryMessageId, context.Deliveries.Last().PrimaryMessageId);
        Assert.Equal(previous.AssociatedMessageIds, context.Deliveries.Last().AssociatedMessageIds);
    }

    [Fact]
    public async Task Rich_edits_use_registered_media_without_an_alternative_mode_retry()
    {
        var (context, bot) = Context();
        await Deliver(context, new DeliverMessage(Rich().Photo(Photo)));
        var previous = context.Deliveries.Last();
        await Deliver(context, new DeliverMessage(Rich().Video(Video), Edit: true, Previous: previous));
        var edit = Assert.Single(bot.RequestsOf<EditMessageTextRequest>());
        Assert.IsType<InputMediaVideo>(edit.RichMessage!.Media!.Single().Media);
        Assert.Single(bot.MessagesIn(42));
        bot.Handle<EditMessageTextRequest>(_ => throw new ApiRequestException("Bad Request: invalid rich message", 400));
        await Assert.ThrowsAsync<ApiRequestException>(() => Deliver(context, new DeliverMessage(Rich(), Edit: true, Previous: previous)));
        Assert.Single(bot.RequestsOf<SendRichMessageRequest>());
        Assert.Empty(bot.RequestsOf<SendMessageRequest>());
    }

    [Fact]
    public async Task Receipts_from_other_chats_and_reply_keyboard_edits_fail_before_sending()
    {
        var (context, bot) = Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Deliver(context,
            new DeliverMessage(Standard().Photo(Photo), Edit: true, Previous: new MessageDelivery(7, 1, [], false))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Deliver(context,
            new DeliverMessage(Standard().ReplyButtons(["Reply"]), Edit: true)));
        Assert.Empty(bot.Requests);
    }

    [Fact]
    public async Task Legacy_edit_errors_only_replace_known_missing_targets()
    {
        var (context, bot) = Context();
        bot.Handle<EditMessageTextRequest>(_ => throw new ApiRequestException("Bad Request: can't parse entities", 400));
        await Assert.ThrowsAsync<ApiRequestException>(() => new TelegramResultExecutor().ExecuteAsync(context,
            new EditText("bad", MessageId: 123), CancellationToken.None));
        Assert.Empty(bot.RequestsOf<SendMessageRequest>());
    }
}
