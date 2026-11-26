# Annotated message factories

Views are discovered in the same assemblies as controllers and use constructor injection. The default lifetime is scoped;
`[View(Lifetime = ServiceLifetime.Singleton)]` remains available. `View` is a convenience base with an instance `Message()`
factory. It has no required override. Public methods can take zero, one, or several arguments and return `Message`,
`Task<Message>` or `ValueTask<Message>`.

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

## Asynchronous screens

Load data and build the screen in one method. The controller explicitly awaits construction and chooses delivery:

```csharp
[View(Rich = true)]
public sealed class ProfileView(IProfileService profiles) : View
{
    public async Task<Message> OpenAsync(long userId, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(userId, ct);
        return Message().Heading(profile.Name).Paragraph(profile.Description);
    }
}

public sealed class ProfileController(ProfileView view) : BotController
{
    [Command("profile")]
    public async Task<IBotResult> Open(CancellationToken ct) =>
        Reply(await view.OpenAsync(Context.UserId!.Value, ct));
}
```

`ValueTask<Message>` works identically. `Message()` keeps the declared screen name after asynchronous waits;
no synchronous adapter, screen-name argument or extra model is needed. Mode annotations, inheritance and unique-name
checks apply to all three return types. Startup validation and caching inspect metadata only, never invoke a screen.
Construction errors and cancellation remain action errors/cancellation in the existing pipeline; no screen delivery
happens before construction succeeds. Synchronous screens are unchanged.

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
`Message`, `Task<Message>` or `ValueTask<Message>` must have unique names across their inheritance hierarchy. Overloads and hidden names are
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

## User selection

Structured messages support Telegram's user-selection reply button without falling back to `SendText`:

```csharp
return Reply(contacts.Select().RequestUsers("Select users", new KeyboardButtonRequestUsers(17)
{
    MaxQuantity = 10,
    UserIsBot = false,
    UserIsPremium = null,
    RequestName = true,
    RequestUsername = true,
    RequestPhoto = true
}));
```

Import `Telegram.Bot.Types.ReplyMarkups` for `KeyboardButtonRequestUsers`. All request fields are preserved, including
`false`/unspecified filters. Options are copied when declaring the message and again when evaluated, so mutating the
input or a previously rendered keyboard cannot change a reusable message. `MaxQuantity`, when supplied, must be 1-10;
request IDs must be unique within the keyboard. Each call appends one reply-keyboard row, compatible with the existing
contact, location and reply buttons. User selection is available in private chats; rich messages and edits cannot use
reply keyboards. Legacy `MessageBuilder.RequestUsers(text, request)` supports the same fields and validation.

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

After a confirmed delivery, callback, finalizer and cleanup errors use `MessageDeliveryException`. Its `Delivery`
contains the actual receipt, and its inherited `InnerExceptions` preserve the errors (`Flatten()` includes nested
execution/finalization errors). `IBotMessageSender` exposes the same exception, including when cancellation occurs
after confirmation. Recover application bookkeeping using that receipt; do not repeat the send. Failures before
confirmation retain their original exceptions and partial-output cleanup behavior.

## Delivery lifecycle

`IBotResult.OnDelivered((receipt, ct) => ...)` observes actual receipts for all message results, including `SendText`
with arbitrary `ReplyMarkup`, media, invoices, edits and replacement sends:

```csharp
return new SendText("Choose users", keyboard)
    .OnDelivered((receipt, ct) => tracker.SaveAsync(receipt, ct))
    .Finally(() => gate.Release());
```

For `MultiResult`, the callback runs once per confirmed receipt, in result order, before the next item executes.
A structured message with associated media produces one receipt containing all IDs. An unchanged edit confirms the
existing ID; a fallback send reports the replacement ID. Empty results, deletions and failed/rolled-back sends emit no
delivery callback. Receipts from previous executions of the context are not replayed. A later send failure leaves
earlier notifications intact and retains its original sending exception.

Result observers run after the individual message result's execution and its message-level callbacks/finalizers, even
if that execution reported post-delivery errors. Multiple observers run in declaration order and are all attempted for
the confirmed receipt. Observer failures use `MessageDeliveryException`, retain earlier errors and stop the sequence
before the next result. Enclosing result `Finally` callbacks run after the delivery observers regardless of fluent-call
order. Observers receive the delivery cancellation token; finalizers continue to receive an independent token.

`Finally` runs after a result has been attempted, including validation/API errors, callback errors and cancellation.
It receives `CancellationToken.None` so shutdown cancellation cannot skip releasing an application lock:

```csharp
return Reply(profile.Profile(model)
    .OnDelivered((receipt, ct) => tracker.SaveAsync(receipt, ct)))
    .DeleteAfter(TimeSpan.FromMinutes(5))
    .Finally(() => gate.Release());
```

Both `IBotResult` and `Message` offer `Finally(Action)`, `Finally(Func<Task>)` and
`Finally(Func<CancellationToken, Task>)`. A finalizer attached to a `MultiResult` covers the whole sequence,
including a failure in an earlier item. A result never reached is not executed or finalized. Repeated finalizers all
run; finalizer errors propagate, and an execution error plus a finalizer error are preserved in an `AggregateException`
(`MessageDeliveryException` derives from it for confirmed deliveries). A later send failure in a sequence is still a
send failure even if an earlier item was delivered; `BotContext.Deliveries` retains those earlier receipts.

`DeleteAfter(TimeSpan)` is available on `IBotResult` and `Message`, including background sends and broadcasts.
Deletion is scheduled as soon as an actual receipt exists, before cleanup and `OnDelivered`, so callback failure or
cancellation does not prevent expiry. It deletes the primary and every associated media ID, independently attempting
each deletion. Missing messages are ignored; other deletion errors are logged without retry. No output means no timer.
For a wrapped sequence, all delivered items expire; an inner message's explicit delay overrides the outer delay.
The delay accepts zero through 4294967294 milliseconds. Scheduling uses the registered `TimeProvider` and a hosted
in-memory queue: pending deletions are lost on shutdown/restart. Telegram permissions and deletion limits still apply.
Editing a message does not cancel a previously scheduled deletion of its IDs.

After a successful action, conversation state is saved before result execution as before. Changes made later by
`OnDelivered` or `Finally` are saved by the update dispatcher after the complete result chain, including finalizers
added by outer application middleware, before the update scope/conversation lock is released. These later mutations
are saved even if delivery, cleanup or a callback throws, or delivery is cancelled; partial callback changes are retained.
Action failures still do not save action changes. Post-delivery storage uses a non-cancelled token; storage failures
propagate to the dispatcher and are logged, retaining the delivery error as well if both fail. Delivery does not roll
back action state. Only modified state is saved; clearing it removes the stored record. Background sends have no
conversation state; their callbacks own any application persistence.

Broadcasts count every confirmed chat exactly once in `Sent`. `BroadcastReport.PostDeliveryErrors` maps chats with
post-delivery errors (including `BroadcastOptions.OnSent`) to `MessageDeliveryException` with receipts and original
errors. It is a diagnostic subset of `Sent`, not another progress category. Such chats never enter `Failed`,
`Unreachable` or `Remaining`, including on cancellation, so resuming from `Remaining` does not duplicate their messages.
`OnSent` is attempted for a confirmed delivery even if its earlier callback failed; its existing cancellation token
is preserved and any resulting cancellation error is recorded as a post-delivery error. Sending errors before
confirmation retain their previous failed/unreachable/cancelled classification and retry behavior.

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
hooks continue to use `BotMessage`/`Build` for compatibility; broadcasting supports both message APIs.

Source compatibility considerations: adding `Views.Message` may require a type alias where Telegram's `Message` is also
imported. `[View]` annotations now inherit mode and lifetime. Editing no longer treats every 400 as permission to send a
replacement; code relying on that behavior must handle real errors. Existing rendered `BotMessage` values do not gain
structured gallery metadata; legacy result execution now records single-message receipts too. Migrate gallery screens
to `Message` instead of mixing attachment side channels.
