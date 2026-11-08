# TelegramBotForge

Build Telegram bots in .NET the way you build ASP.NET Core apps: **controllers** with attribute routes, **views** that describe messages, **results** that the library sends for you, plus **middleware** and **conversation state** for forms.

```csharp
public sealed class StartController(MainMenuView menu) : BotController
{
    [Command("start")]
    public IBotResult Start() => Reply(menu.Render());

    [Callback("menu")]
    public IBotResult Menu() => Edit(menu.Render());
}
```

> **Status: 0.2 preview.** The API can still change before 1.0.

## Packages

| Package | What it is for |
|---|---|
| `TelegramBotForge` | The library: long polling, routing, views, state, Stars payments. |
| `TelegramBotForge.AspNetCore` | Webhook mode and a health check for ASP.NET Core apps. |
| `TelegramBotForge.Testing` | Run your bot in tests without Telegram. |

```
dotnet new worker -n MyBot        # or any app with Microsoft.Extensions.Hosting
cd MyBot
dotnet add package TelegramBotForge
```

Targets .NET 8 and .NET 10 and uses [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot).

## Quick start

**1. Register the bot** in `Program.cs`:

```csharp
using TelegramBotForge;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddTelegramBot(builder.Configuration);
builder.Build().Run();
```

**2. Add the token** from [@BotFather](https://t.me/BotFather). Keep it out of source control:

```
dotnet user-secrets set "TelegramBotConfiguration:Token" "123456:ABC..."
```

**3. Write a controller.** Controllers and views in your project are found automatically.

```csharp
using TelegramBotForge.Controllers;
using TelegramBotForge.Results;

public sealed class HelloController : BotController
{
    [Command("start")]
    public IBotResult Start() => Reply("Hello from TelegramBotForge!");
}
```

## How it works

```
Update → queue (per chat) → middleware → router → action (in a controller) → services + views → result → Telegram
```

- **Controller**: a class deriving from `BotController`. It gets services through its constructor.
- **Action**: a public method of a controller with a route attribute. It returns an `IBotResult`.
- **View**: a class that builds a message (text, buttons, media). Views are registered in DI and injected into controllers.
- **Result**: what should happen (`Reply`, `Edit`, `Answer`, `Delete`, `Invoice`, …). Only the library talks to Telegram, so actions stay easy to test.

## Routes

| Attribute | Matches |
|---|---|
| `[Command("help")]` | `/help` and `/help@YourBot` (not `/help@OtherBot`); what follows is bound to `string args` |
| `[Callback("product:{id:int}")]` | Button data; `{id}` is bound to the `int id` parameter |
| `[ButtonText("🛒 Catalog")]` | A reply-keyboard button: text equal to one of the labels |
| `[Regex(@"^(?<amount>\d+) usd$")]` | Text that matches; named groups are bound to parameters (`decimal amount`) |
| `[Text]` | Any text that is not a command, bound to `string text` |
| `[Media]`, `[Media(MediaType.Photo)]` | Photos, videos, documents and more, bound to `BotMedia` |
| `[Album]` | Several photos or videos sent together, once, bound to `IReadOnlyList<BotMedia>` |
| `[Location]` | A shared location, bound to `Location` (and `Venue`) |
| `[Contact]`, `[Contact(OwnOnly = true)]` | A shared contact, bound to `Contact`; `OwnOnly` for the user's own number |
| `[EditedText]`, `[Edited]` | A message the user edited, bound to `string text` and `Message` |
| `[InlineQuery]` | `@YourBot something` typed in any chat, bound to `string query`; answer with `AnswerInline(...)` |
| `[BotBlocked]`, `[BotUnblocked]` | A user stopped or restarted the bot, bound to `ChatMemberUpdated` |
| `[AddedToChat]`, `[RemovedFromChat]` | The bot was added to or removed from a group or channel |
| `[State(MyForm.Name)]` | Only while the user is at that step of a form |
| `[ChatType(ChatType.Private)]` | Only in chats of those types; otherwise the next matching action runs |
| `[PreCheckout]`, `[Paid("code")]` | Telegram Stars payments |

An action can have several routes. A placeholder that only some routes have needs a default value:

```csharp
[Command("catalog")]
[Callback("catalog:{page:int}")]
public IBotResult Catalog(int page = 1) => ...
```

### Command arguments and deep links

What follows a command is bound to a parameter named `args`, as a `string` or split at spaces as a `string[]`.
A deep link `https://t.me/YourBot?start=ref_42` arrives as `/start ref_42`:

```csharp
[Command("start")]
public IBotResult Start(string? args) =>
    args is { } referrer ? Reply($"You were invited by {referrer}") : Reply("Welcome!");

[Command("ban")]
public IBotResult Ban(string[] args) => ...   // /ban 42 spam → ["42", "spam"]
```

### The `/` menu

Give commands a description and call `.RegisterCommands()`: when the bot starts, the menu is set from your code,
so it never gets out of date. Commands without a description stay hidden. A command limited with `[ChatType]` to private
chats or to groups appears only in that menu.

```csharp
[Command("help", Description = "Show what the bot can do")]
```

This replaces the menu set in @BotFather, so it is opt-in.

### Reply-keyboard buttons and patterns

A reply-keyboard button sends its label as a text message. `[ButtonText]` routes it, with one label per language if you
translate the keyboard. Like commands, it wins over `[State]` actions, so a menu button works in the middle of a form.

`[Regex]` routes text by a pattern. Named groups become parameters, converted like `{placeholders}`; a group that does not
take part in the match gives the parameter its default value. It is tried after commands and buttons, before `[Text]`.

```csharp
[ButtonText("🛒 Catalog", "🛒 Каталог")]
public IBotResult Catalog() => Reply(catalog.Render());

[Regex(@"^(?<amount>\d+(\.\d+)?)\s*(?<currency>usd|eur)$", IgnoreCase = true)]
public IBotResult Convert(decimal amount, string currency) => ...   // "12.5 usd"
```

### Edits, contacts and membership

Edited messages only reach `[EditedText]` and `[Edited]` actions, so editing `/start` never runs it again; without such an
action, edits are ignored. `[Contact(OwnOnly = true)]` takes only the number from a "share my phone number" button
(`KeyboardButton.WithRequestContact`), not a contact forwarded from the address book.

`[BotBlocked]` is the place to mark a user inactive, so broadcasts skip them; nothing can be sent to them anymore, so these
actions usually return nothing. `[AddedToChat]` can greet the group: its result goes to that chat.

```csharp
[BotBlocked]
public Task Blocked() => users.SetActiveAsync(Context.UserId!.Value, false);

[AddedToChat]
public IBotResult Added() => Reply("Hi! Send /help to see what I can do.");
```

### Inline mode

Turn inline mode on in @BotFather with `/setinline`, then answer queries with results the user can pick.
`InlineResults.Article` covers the usual case, a message with a title; any Telegram `InlineQueryResult` works too.
Inline queries have no chat, so `[State]` does not apply, and they don't count against the rate limit.

```csharp
[InlineQuery]
public async Task<IBotResult> Search(string query, InlineQuery inline)
{
    var page = await products.SearchAsync(query, offset: inline.Offset);
    return AnswerInline(
        page.Items.Select(p => InlineResults.Article(p.Id.ToString(), p.Name, $"<b>{p.Name}</b>: {p.Price}⭐", description: p.Summary)),
        isPersonal: false,
        nextOffset: page.NextOffset);   // Telegram asks for the next page with it when the user scrolls
}
```

### Albums

Telegram sends every photo of an album as its own message. With an `[Album]` action in the bot, the queue collects the
items and the action runs once with all of them; the caption is on one of the items. Items count as one album when they
arrive less than `AlbumWaitMilliseconds` apart (500 by default), and the messages sent after an album are processed after it.

```csharp
[Album]
public IBotResult Album(IReadOnlyList<BotMedia> items) =>
    Reply($"Got {items.Count} files. Caption: {items.FirstOrDefault(i => i.Caption is not null)?.Caption ?? "none"}");
```

Without an `[Album]` action, nothing changes: `[Media]` actions get each item separately. With one, an album that no
`[Album]` action takes (for example because of `[State]`) reaches `[Media]` as its first item only.
Collecting needs all items of an album to reach the same instance, which is the case with long polling and with one webhook instance.

### Your own routes

Derive from `RouteAttribute`. `TryMatch` decides whether the update is for the action and can fill values that are bound
to parameters with the names in `ParameterNames`. `Priority` decides the order: commands and buttons 100, `[Regex]` 30,
media 20–30, text 10; `[State]` adds 50. `Key` finds two actions with the same route at startup.

```csharp
public sealed class HashtagAttribute(string tag) : RouteAttribute
{
    public override int Priority => 25;
    public override string Key => "hashtag #" + tag;
    public override IReadOnlyList<string> ParameterNames => ["rest"];

    public override bool TryMatch(BotContext context, IDictionary<string, string> values)
    {
        if (context.Text?.Contains("#" + tag) != true)
            return false;
        values["rest"] = context.Text.Replace("#" + tag, "").Trim();
        return true;
    }
}

[Hashtag("news")]
public IBotResult News(string rest) => ...
```

To limit an existing route instead of making a new one, use a `BotConstraintAttribute` (see `[ChatType]`) or a filter.

### Action parameters

Besides the values of its route, an action can take the update itself (`BotContext`, `Message`, `CallbackQuery`, `User`,
`Chat`, `CancellationToken`…) and any registered service, like ASP.NET Core actions. Services most actions need belong in
the constructor; a service only one action needs can be a parameter, optionally marked `[FromServices]`:

```csharp
[Callback("order:{id:int}")]
public async Task<IBotResult> Order(int id, IOrders orders) => ...
```

Problems such as a parameter with no source, an unknown placeholder or two actions with the same route stop the bot at startup with a list of every problem.

### Answers to button presses

By default a pressed button is answered as soon as it arrives, so its spinner stops even when the bot is busy. Telegram
takes one answer per press, so `Answer("Saved!")` and alerts are then not shown (the bot logs a warning once). To show
them, call `.AckCallbacks(CallbackAckMode.Manual)`: actions answer with `Answer(...)`, and buttons they don't answer are
answered after the action.

## Filters

A filter runs around chosen actions after routing: authorization, "subscribers only", auditing. To stop the action,
return a result without calling `next()`. Filters come from DI, so they can take services:

```csharp
public sealed class AdminOnlyFilter(IAdminList admins) : IBotActionFilter
{
    public async Task<IBotResult?> OnActionAsync(BotActionContext action, BotActionNext next, CancellationToken ct) =>
        await admins.ContainsAsync(action.Context.UserId, ct) ? await next() : new SendText("Admins only.");
}

[UseFilter<AdminOnlyFilter>]               // every action of the controller
public sealed class AdminController : BotController { ... }
```

Put `[UseFilter<T>]` on an action for one action only, derive from `BotFilterAttribute` for a filter that needs no
services, or call `.AddFilter<T>()` at startup for every action. They run in that order: startup, controller, action.
Filters added at startup skip `[PreCheckout]` and `[Paid]` actions, so a payment is never lost to them.

`[ChatType]` is not a filter but a condition of the route: an update that doesn't meet it goes on to the next action,
so `/where` can have one action for private chats and another for groups. Derive from `BotConstraintAttribute` for your own.

## Views

```csharp
using TelegramBotForge.Views;

public sealed record Product(int Id, string Name, decimal Price);

public sealed class ProductView : View<Product>
{
    protected override void Build(MessageBuilder message, Product product) => message
        .Line($"<b>{product.Name}</b> — {product.Price:0.00} $")   // values are HTML-escaped
        .Row(Button("🛒 Add to cart", $"cart:add:{product.Id}"))
        .Back("catalog:1");
}
```

Views can also carry a reply keyboard, the buttons under the input field: `.ReplyButtons(["☕ Menu", "👤 Profile"], perRow: 2)`,
`.RequestContact("📞 Share phone")`, `.RequestLocation(...)` and `.RemoveKeyboard()`. Route those buttons with `[ButtonText]`.

Lists get pagination with one call:

```csharp
message.Page(products, page, pageSize: 5,
    item: p => Button(p.Name, $"product:{p.Id}"),
    route: page => $"catalog:{page}");
```

When a page needs more than one button per item, pass `renderPage` instead of `item`. The items of the page are
picked for you; your renderer adds whatever the page shows, and the pagination row comes after it:

```csharp
message.Page(orders, page, pageSize: 5,
    renderPage: (body, visible, info) =>
    {
        if (visible.Count == 0)
        {
            body.Line($"No orders yet.");
            return;
        }

        body.Line($"<b>Orders {info.FirstItemNumber}–{info.LastItemNumber} of {info.TotalItems}</b>");
        foreach (var order in visible)
            body.Row(Button($"#{order.Id} · {order.Total}⭐", $"order:{order.Id}"), Button("❌", $"cancel:{order.Id}"));
    },
    route: p => $"orders:{p}",
    configure: options => options.Style(PaginationStyle.FirstLast));
```

Pages start at 1, and a page out of range shows the nearest one. An empty list is one empty page: the renderer gets no
items, so it can say so, and the row is hidden unless `ShowSinglePage()` is set. In a rich view (`[View(Rich = true)]`)
a page can also be a whole table: `body.Line(Raw("<table>…</table>"))`, with values HTML-escaped by you.

Labels, edge behavior and layout can be changed per call, per view (`ConfigurePagination`) or for the whole bot (`ConfigureViews`).

Telegram's limits (message length, button data size, buttons per row) are checked when a view is rendered, not when Telegram rejects the request.

## Forms and conversation state

Each step of a form is one action, limited to its step with `[State]`:

```csharp
public enum Signup { Name, Age }

public sealed class SignupController : BotController
{
    [Command("signup")]
    public IBotResult Start() => EnterState(Signup.Name, "What's your name?");

    [State(Signup.Name), Text]
    public IBotResult OnName(string text)
    {
        State.Set("name", text);
        return EnterState(Signup.Age, "How old are you?");
    }

    [State(Signup.Age), Text]
    public IBotResult OnAge(string text) =>
        int.TryParse(text, out var age)
            ? ExitState($"Thanks, {State.Get<string>("name")}!")
            : Reply("Please send a number.");
}
```

States are kept in memory by default, so they are lost when the bot restarts and are not shared between instances.
To keep them in Redis, a database or anything else, implement `IStateStorage` (three methods: get, save with a time to live,
remove) and register it with `.UseStateStorage<MyStateStorage>()`. The library does not ship storage packages, so you
choose your own database and client.

## Telegram Stars

```csharp
builder.Services.AddTelegramBot(builder.Configuration)
    .AddStars(stars => stars
        .Product("premium_month", "Premium", "30 days of Premium", price: 100));
```

```csharp
[Callback("buy:{code}")]
public IBotResult Buy(string code) => Invoice(code);

[Paid("premium_month")]
public IBotResult OnPremium(StarsPayment payment) => Reply("⭐ Premium is active!");
```

The library checks every order before Telegram's 10-second deadline, ignores repeated payment notices, answers `/paysupport` and refunds with `Refund(chargeId)`. Load products from a database with `UseStarsProducts<T>()`.

Repeated payment notices are recognized by their charge id, kept in memory for 30 days by default. In production, and
always with more than one instance, store them in your database: implement `IProcessedPayments` and register it with
`UseProcessedPayments<T>()`.

## Localization

```csharp
builder.Services.AddTelegramBot(builder.Configuration)
    .UseLocalization(options =>
    {
        options.DefaultCulture = "en";
        options.SupportedCultures = ["en", "ru", "ka"];
    });
```

Every update then runs in the user's language: `CultureInfo.CurrentUICulture` (and `CurrentCulture`) is set, so
`IStringLocalizer`, `.resx` resources and formatting work as in ASP.NET Core. The library doesn't pick a translation
system for you. By default the language is the one of the user's Telegram app. A language that isn't supported falls back to its
parent (`pt-br` → `pt`), then to the default. An action can take a `CultureInfo` parameter, and middleware can read `context.Culture`.

When users pick a language in your bot, return it from your own resolver. `null` falls back to the Telegram language:

```csharp
public sealed class UserLanguageResolver(IUserRepository users) : IBotCultureResolver
{
    public async ValueTask<string?> GetLanguageAsync(BotContext context, CancellationToken ct) =>
        context.UserId is { } id ? (await users.FindAsync(id, ct))?.Language : null;
}

.UseCultureResolver<UserLanguageResolver>()
```

With `RegisterCommands()`, set `options.TranslateCommand = (description, culture) => ...` to register the `/` menu in
every supported language. The description is then a text or a resource key.

## Built-in messages

The library sends a few messages itself: unknown command, error, rate limit, `/paysupport` and declined payment. To change one, for example to translate it, derive from its view. Your class is found and used instead:

```csharp
public sealed class MyUnknownCommandView : UnknownCommandView
{
    protected override void Build(MessageBuilder message) => message
        .Line($"I don't know that one. Try /help.");
}
```

## Configuration vs code

Settings that change between environments are in configuration, in the `TelegramBotConfiguration` section:

```json
{
  "TelegramBotConfiguration": {
    "Token": "from user secrets or an environment variable",
    "MaxGlobalConcurrency": 32,
    "RateLimit": { "Enabled": true, "MaxUpdates": 30, "PerSeconds": 60 },
    "FloodControl": { "GlobalPerSecond": 30, "PrivateChatPerSecond": 1, "PrivateChatBurst": 3, "GroupPerMinute": 20 },
    "Stars": { "PaySupportMessage": "Problems with a purchase? Write to @MyShopSupport." },
    "Logging": { "MinimumLevel": "Information" }
  }
}
```

`RateLimit` limits what users send to the bot. `FloodControl` (on by default) keeps what the bot sends under Telegram's
limits: new messages wait for their turn per chat and overall, so a busy bot or a broadcast doesn't get
`429 Too Many Requests`. If a 429 still comes, the request is repeated after the wait Telegram asks for
(`RetryCount`, `MaxRetryAfterSeconds`). Edits and button answers are not delayed.

Decisions your code relies on are set in code:

```csharp
builder.Services.AddTelegramBot(builder.Configuration)
    .AckCallbacks(CallbackAckMode.Manual)        // actions answer buttons with Answer(...)
    .QueueBy(QueueKeyStrategy.ChatAndUser)       // users in a group don't wait for each other
    .KeepStateFor(TimeSpan.FromHours(6))         // unfinished forms expire
    .UseMiddleware<EnsureUserMiddleware>();
```

## Webhooks

```csharp
// dotnet add package TelegramBotForge.AspNetCore
builder.Services.AddTelegramBot(builder.Configuration).UseWebhook();

var app = builder.Build();
app.MapTelegramBotWebhook();
app.Run();
```

```json
"TelegramBotConfiguration": {
  "Webhook": { "Url": "https://bot.example.com/telegram/webhook", "SecretToken": "a-long-random-string" }
}
```

## Broadcasts

Inject `IBotBroadcaster` anywhere (a background job, an admin action, an endpoint) to message many chats:

```csharp
var report = await broadcaster.SendAsync(
    await users.ActiveIdsAsync(ct),
    newsView.Render(post),
    new BroadcastOptions
    {
        OnUnreachable = (chatId, ct) => users.MarkInactiveAsync(chatId, ct), // blocked the bot, deleted account, removed from group
        OnSent = (chatId, messageId, ct) => deliveries.RecordAsync(post.Id, chatId, ct),
        Progress = new Progress<BroadcastProgress>(p => logger.LogInformation("{Done}/{Total}", p.Done, p.Total))
    },
    ct);
```

It sends at 25 messages per second by default (Telegram allows about 30), so the bot can still answer users during
a broadcast. It never throws for one chat: the report has `Sent`, `Unreachable`, `Failed` (with the error) and `Skipped`.
Cancelling returns the report too. To resume, send again to `report.Remaining`; to survive a restart, record deliveries in
`OnSent` and leave those chats out next time. To send each user a message in their language, pass a function instead:
`SendAsync(ids, (chatId, ct) => RenderFor(chatId, ct))`. Return `null` to skip a chat.

## Monitoring

**Metrics and traces.** The library publishes OpenTelemetry-compatible metrics and traces through .NET's built-in
`Meter` and `ActivitySource`, both named `TelegramBotForge`. Nothing is recorded until you listen:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(BotTelemetry.MeterName).AddPrometheusExporter())
    .WithTracing(t => t.AddSource(BotTelemetry.ActivitySourceName).AddOtlpExporter());
```

| Metric | What it shows |
|---|---|
| `telegrambot.updates` | Processed updates by `update.type` and `outcome` (handled, ignored, failed) |
| `telegrambot.update.duration` | Seconds to process an update and send the answer, by action |
| `telegrambot.api.requests` / `.api.duration` | Bot API calls by method and outcome (ok or the error code) |
| `telegrambot.floodcontrol.wait` | How long messages waited for Telegram's limits |
| `telegrambot.broadcast.messages` | Broadcast messages by outcome |
| `telegrambot.queue.length` | Updates waiting to be processed |

Each update is a `telegram.update` trace with a child span for every Bot API call, so a slow answer shows where the time went.

**Health check** (`TelegramBotForge.AspNetCore`):

```csharp
builder.Services.AddHealthChecks().AddTelegramBot();
app.MapHealthChecks("/health");
```

It is unhealthy when Telegram rejects the token, the webhook could not be registered, or Telegram cannot be reached, and
degraded when another process polls with the same token or Telegram reports webhook delivery errors. Telegram is asked
at most every 30 seconds.

## Running several instances

Inside one process, the updates of a conversation are processed one at a time and in order. With webhooks behind a
load balancer, two instances can receive updates of the same chat at the same moment: answers can arrive out of order,
and conversation state written by one can be overwritten by the other. (Long polling always runs as a single
instance, because Telegram gives updates to only one poller.)

Give the bot a distributed lock, for example in Redis, and every update of a conversation runs inside it:

```csharp
.UseConversationLock<RedisConversationLock>()   // implements IConversationLock; an example is in its XML docs
```

The conversation state then needs shared storage too (`UseStateStorage<T>()`), and so does payment
de-duplication (`UseProcessedPayments<T>()`). Checks before payment don't take the lock, because Telegram allows them
only 10 seconds.

## Testing

```
dotnet add package TelegramBotForge.Testing
```

`BotTestHost` runs your real controllers, views, middleware, filters, state and payments against a fake Telegram client.
It works with any test framework:

```csharp
[Fact]
public async Task Catalog_opens_from_the_menu()
{
    await using var bot = BotTestHost.Create<StartController>(
        services => services.AddSingleton<IProducts, FakeProducts>(),
        bot => bot.AddStars(stars => stars.Product("premium", "Premium", "30 days", price: 100)));
    var user = bot.PrivateChat(languageCode: "en");

    Assert.Equal("Welcome!", (await user.SendText("/start")).Text);

    await user.PressButton("🛒 Catalog");            // by its label, on the newest message that has it
    Assert.Contains("Page 1", user.LastMessage!.Text);

    await user.SendCallback("buy:premium");
    var payment = await user.PayInvoice();           // the check before payment, then the payment
    Assert.True(payment.Accepted);
}
```

Each call returns when the bot has answered, with the requests it made (`Text`, `Texts`, `Of<SendInvoiceRequest>()`,
`CallbackAnswer`, `InlineAnswer`…). Besides text and buttons, a `TestChat` can `SendContact`, `SendAlbum`, `EditText`,
`SendInlineQuery`, `BlockBot`/`UnblockBot` and, in a group, `AddBot`/`RemoveBot`. `user.Messages` shows the chat as it looks after edits and deletes. To simulate Telegram errors,
use `bot.Telegram.Handle<SendMessageRequest>(_ => throw new ApiRequestException("Forbidden: bot was blocked by the user", 403))`.

## Roadmap

Not supported yet, planned in this order:

- **Mini Apps**: validate the `initData` a Mini App sends to your backend (Telegram's HMAC signature and its age) in
  TelegramBotForge.AspNetCore, and give the endpoint the verified user. Handle `web_app_data` from a Mini App's
  `sendData` with a route.
- **Admin features** for group bots: `[JoinRequest]` with approve and decline results, `[UserJoined]` and `[UserLeft]`,
  results to ban, mute (restrict) and unban members and to pin messages, and a `[ChatAdmin]` condition that checks the
  user is an administrator of the group (cached).
- **Form timeouts**: remind or reset a user who leaves a form unfinished, instead of the state expiring silently.
- **Faster actions**: call actions through compiled delegates instead of reflection.
- **A documentation site**, with one page per topic.

Not planned: storage packages (implement `IStateStorage` and `IProcessedPayments` for your database), a translation
system (use `.resx` or `IStringLocalizer` with `UseLocalization`), and scheduled jobs (use Quartz.NET or Hangfire with
`IBotBroadcaster`).

## Contributing

Each package lists its public API in `PublicAPI.Shipped.txt`. A change to the public API makes the build fail until it is
added to `PublicAPI.Unshipped.txt` (the IDE offers the fix, or run
`dotnet format analyzers --diagnostics RS0016`), so API changes are always deliberate and visible in review.
When a version is released, move its lines to `PublicAPI.Shipped.txt` and describe it in [CHANGELOG.md](CHANGELOG.md).

## License

[MIT](https://github.com/VAXA99/TelegramBotForge/blob/main/LICENSE)
