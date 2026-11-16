# Annotated message factories

Views are discovered in the same assemblies as controllers and use constructor injection. The default lifetime is scoped;
`[View(Lifetime = ServiceLifetime.Singleton)]` remains available. `View` is a convenience base with an instance `Message()`
factory. It has no required override. Public methods can take zero, one, or several arguments and return a message directly.

```csharp
using TelegramBotForge.Views;

public sealed class MenuView : View
{
    public Message Start(string name) => Message()
        .Heading($"Welcome, {name}")
        .Paragraph("Choose an action")
        .Row(Buttons.Callback("Continue", "start:next").Style(ButtonStyle.Primary));
}

public sealed class MenuController(MenuView menu) : BotController
{
    [Command("start")]
    public IBotResult Start() => Reply(menu.Start("Guest"));

    [Callback("menu")]
    public IBotResult Menu() => Edit(menu.Start("Guest"));
}
```

There is no renderer dependency, generated companion method, rendering option, or terminal build call.
`Reply(string)` and `Edit(string)` retain their existing HTML behavior; use a message factory for escaped text.

## Mode and method identity

```csharp
[View(Rich = true)]
public sealed class NavigationView(ILocalizationService localization) : View
{
    public Message Start(string title, string greeting) => Message()
        .Heading(title).Paragraph(greeting);

    [Standard]
    public Message Support() => Message()
        .Heading(localization.Get("Support.Title"))
        .Paragraph(localization.Get("Support.Description"))
        .Link("Support", "https://t.me/MeetAuraSupportBot");
}

[View]
public sealed class CatalogView : View
{
    [Rich]
    public Message Product(string name) => Message().Heading(name);
}
```

The nearest explicit method annotation wins, then the nearest inherited `[View]` annotation, then standard mode.
An override with no annotation inherits its overridden method's annotation; an explicit annotation on an override replaces it.
A method declaring both `[Rich]` and `[Standard]`, including a conflicting declaration in its override chain, fails validation.
Class annotation inheritance includes the DI lifetime.

`Message()` uses compiler-supplied `CallerMemberName` and the runtime view type. Metadata is cached per type, validated during
automatic discovery, and also validated lazily for directly constructed views. Public instance screen methods returning
`TelegramBotForge.Views.Message` must have unique names across their inheritance hierarchy. Overloads and hidden names are
rejected; generic screen methods are not supported. Overrides represent the same screen. There is no stack inspection or
ambient rendering state. Call the factory directly from the screen method, not from a lambda, property, or private helper.
Private helpers append to a supplied message and return the resulting message.

Inheritance is optional. A class marked `[View]` can instead start a screen with `Message.Create<MyView>()`; this static
factory resolves the enclosing method's annotations using the explicit view type and compiler-supplied caller name.

`Message` is the library's declaration type; `BotMessage` is the legacy evaluated payload. When importing Telegram types
in the same file, use `using Message = TelegramBotForge.Views.Message;` and qualify Telegram's received message type.
Likewise, qualify `TelegramBotForge.Views.Text` if your application already has a `Text` type.

## Content and fallbacks

Strings always represent text and are escaped, including interpolated strings passed to `Heading` or `Paragraph`.
`Text` represents immutable inline content:

```csharp
message.Paragraph(Text.Concat(
    Text.Bold("Name: "), Text.Italic(name), " ", Text.Emoji(emojiId, "*")))
    .Paragraph(Text.Join(", ", values.Select(value => Text.Plain(value))))
    .Paragraph(Text.Underline("Important"))
    .Paragraph(Text.Strike("Old"))
    .Paragraph(Text.Code("inline code"))
    .Paragraph(Text.Preformatted("code\nblock"));
```

| Declaration | Rich | Standard |
|---|---|---|
| `Heading` | Section heading | Bold with paragraph spacing |
| `Paragraph` | Paragraph | Formatted text with paragraph spacing |
| `Space` | Explicit line break | Explicit empty line |
| `Table(t => t.Field(label, value).Row(a, b, c))` | Compact table | Bold label/value fields or pipe-separated rows |
| `Details(summary, body)` | Collapsible section | Summary followed by expanded body |
| `Text.Emoji(id, fallback)` | Custom emoji | Custom emoji entity with fallback text |
| `Photo`, `Video`, `Slideshow` | Embedded registered media | Individual media messages followed by a primary text/control message |
| `Row`, `Buttons` | Embedded rows, in declaration order | Rows collected below the text as an inline keyboard, in declaration order |

Callback, URL and copy buttons use the existing `Buttons.Callback`, `Buttons.Url`, and `Buttons.Copy` factories. Styles
and `.Icon(id, fallback)` work with both representations. `.Label(Text.Concat("Next ", Text.Emoji(id, "*")))` composes a
label from plain text and custom emoji. Standard labels flatten this content to text, retaining emoji fallback text.
Links, emphasis, code and preformatted blocks in composed button labels fail validation. Rich labels cannot nest buttons.
Disabled buttons are supported in rich mode; in standard mode they fail descriptively. For decorative badges that must
work in both modes, use text/custom emoji inside a heading or paragraph instead of a disabled control.

`ReplyButtons`, `RequestContact`, `RequestLocation` and `RemoveKeyboard` work in standard mode. Mixing inline and reply
keyboards fails, and edits accept only inline keyboards.
Pagination reuses `PaginationOptions`, `PageInfo`, `PageWindow`, and view/application defaults:

```csharp
public Message Products(IReadOnlyList<Product> products, int page) => Message()
    .Heading("Products")
    .Page(products, page, 5,
        (body, visible, info) => body.Buttons(visible,
            product => Buttons.Callback(product.Name, $"product:{product.Id}")),
        pageNumber => $"products:{pageNumber}");
```

Validation checks callback bytes (1-64), row size (8), total buttons (100), keyboard compatibility, HTML structure,
registered media references and supported inline combinations. Standard primary text permits 4096 UTF-16 code units;
structured standard media is separate and has no caption limit to consume. Legacy media captions retain the 1024 limit.
Rich validation uses the [Telegram rich message limits](https://core.telegram.org/bots/api#rich-message-formatting-options):
32768 text characters, 500 blocks, 16 nesting levels, 50 attachments and 20 table columns. Standard structured delivery
is limited to 10 attachments per screen. Unknown raw tags and detectable invalid combinations fail before any API call.
Preformatted content needs its own paragraph; standard code cannot be nested inside other inline formatting.
Telegram still applies permission, media file, and server-side validation. API exceptions carry the screen origin in
`exception.Data["TelegramBotForge.View"]`; content errors include the view and method in their message.

## Components and profile-style media

Components are application-owned functions that append to the supplied immutable message. They run once during declaration,
inherit the parent's mode, and contribute text, layout, buttons, media and nested components to the same validation/delivery.
They return the fluent result; ignoring a fluent return value does not mutate the original message. Attach delivery
callbacks to the root; a component cannot register its own delivery callback.

```csharp
public sealed record ProfileModel(string Name, string Bio, string Age, string City,
    string Preferences, IReadOnlyList<MediaFile> Media);

public static class ProfileCard
{
    public static Message Append(Message body, ProfileModel model) => body
        .Heading(model.Name)
        .Paragraph(Text.Bold(model.Bio))
        .Table(table => table.Field("Age", model.Age).Field("City", model.City))
        .Details("Preferences", section => section.Paragraph(model.Preferences))
        .Slideshow(model.Media);
}

[View(Rich = true)]
public sealed class ProfileView : View
{
    public Message Profile(ProfileModel model) => Message()
        .Component(body => ProfileCard.Append(body, model))
        .Row(Buttons.Callback("Next", "profiles:next").Style(ButtonStyle.Primary));
}
```

The media collection contains `MediaFile` values, for example `MediaFile.FromFileId(MediaType.Photo, fileId)`;
URLs and local paths also work. The library opens and disposes local streams during execution, creates rich attachment
IDs and references, and validates their correspondence. Components cannot switch mode or return another view's root.
No business rules, media selection, localization keys or product-specific emoji IDs belong in the library.

## Explicit raw HTML

```csharp
public Message Custom(string title, string trustedHtml, string description) => Message()
    .Heading(title).RawHtml(trustedHtml).Paragraph(description);

[Standard]
public Message Notice(string trustedHtml) => Message().RawHtml(trustedHtml);
```

Raw insertion preserves markup unchanged and inherits the root's annotated mode. It still passes through payload
validation, normal delivery and receipts. Quote attribute values; supported HTML boolean attributes and void tags are
accepted. Raw fragments may share formatting across fragment boundaries if the complete message is valid.
Standard raw markup must be Telegram standard HTML. Rich tables/details cannot automatically become standard HTML;
write structured content when a fallback is needed. Unknown tags, invalid nesting and unregistered `tg://` media
references fail descriptively. Raw HTML is trusted input, not a sanitizer or an unrestricted browser HTML surface.

## Reuse, receipts and edits

Descriptions, inline content, buttons and tables are immutable. Fluent operations copy input collections. Component
functions and enumerable inputs are evaluated during declaration, so later changes cannot alter queued delivery.
Descriptions can be reused concurrently; application callbacks and captured application data must themselves be safe.

Every structured delivery adds a `MessageDelivery` receipt to `BotContext.Deliveries` in result order. Alternatively,
attach `.OnDelivered((receipt, ct) => tracker.SaveAsync(receipt, ct))` to a message. Receipts contain the actual chat ID,
primary message ID, associated media IDs and mode. The primary is always the message containing text/interactive controls;
a media-only standard screen uses an invisible text anchor. Persist receipts in application storage as needed.

```csharp
return Edit(profile.Profile(model)
    .OnDelivered((receipt, ct) => tracker.SaveAsync(receipt, ct)), previousReceipt);
```

`Edit(message)` defaults to the callback message; `DeliverMessage` also accepts an explicit `MessageId`. Pass the previous
receipt when updating a standard screen with associated media, including when removing all media. Without that receipt,
the library cannot discover earlier associated outputs. It does not keep a global message-ID cache. Live receipts reuse
unchanged file-ID media. Restored receipts, local paths and URLs cause media replacement, followed by cleanup of the previous
IDs. Receipts support `System.Text.Json` round trips. New media is followed by a new primary message so controls remain last.

An in-memory receipt retains immutable media descriptors so unchanged media can be reused; a reconstructed receipt safely
replaces associated media. Changed media is sent before editing the primary, then obsolete media is deleted. Mode changes
with a previous receipt replace the primary and delete old output. An unchanged edit preserves the target ID. A missing
or explicitly uneditable target produces a replacement and cleanup. Other 400 errors propagate without a replacement or
mode retry. New media is rolled back when a primary operation fails. Successful receipts are recorded before cleanup and
notification callbacks, so recovery code can inspect actual output if either fails. Telegram operations are not atomic:
cleanup/network failures propagate, and the receipt IDs remain available for application recovery.

## Migration

Before:

```csharp
public sealed class WelcomeView : View<string>
{
    protected override void Build(MessageBuilder message, string name) => message
        .Line($"<b>Welcome</b> {name}")
        .Row(Button("Next", "next"));
}
// controller: Reply(welcome.Render(name))
```

After:

```csharp
public sealed class WelcomeView : View
{
    public Message Welcome(string name) => Message()
        .Heading("Welcome").Paragraph(name)
        .Row(Buttons.Callback("Next", "next"));
}
// controller: Reply(welcome.Welcome(name))
```

`View<T>`, `Build`, `Render`, `IView`, `MessageBuilder`, `BotMessage`, legacy result factories and controller overloads
remain compatibility APIs. `View.Build` becomes virtual with a descriptive default failure if legacy `Render()` is called
on a new factory-only view. Legacy `Line($"...")` still treats literal segments as HTML and escapes interpolations;
`RawHtml`, `Raw(...)`, and `Line(Raw(...))` retain their behavior. New message strings always escape their entire value.
Both paths share button serialization, callback/keyboard checks and the result executor. Built-in system-view override
hooks and broadcasting continue to use `BotMessage`/`Build` for compatibility.

Source compatibility considerations: adding `Views.Message` may require a type alias where Telegram's `Message` is also
imported. `[View]` annotations now inherit mode and lifetime. Editing no longer treats every 400 as permission to send a
replacement; code relying on that behavior must handle real errors. Existing rendered `BotMessage` values do not gain
structured gallery metadata or receipts; migrate gallery screens to `Message` instead of mixing attachment side channels.
