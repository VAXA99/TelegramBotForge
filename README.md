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

> **Status: 0.1 preview.** The API can still change before 1.0.

## Packages

| Package | What it is for |
|---|---|
| `TelegramBotForge` | The library: long polling, routing, views, state, Stars payments. |
| `TelegramBotForge.AspNetCore` | Webhook mode for ASP.NET Core apps. |

```
dotnet add package TelegramBotForge
```

Targets .NET 8 and uses [Telegram.Bot](https://github.com/TelegramBots/Telegram.Bot).

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
| `[Command("help")]` | `/help` and `/help@YourBot` |
| `[Callback("product:{id:int}")]` | Button data; `{id}` is bound to the `int id` parameter |
| `[Text]` | Any text that is not a command, bound to `string text` |
| `[Media]`, `[Media(MediaType.Photo)]` | Photos, videos, documents and more, bound to `BotMedia` |
| `[Location]` | A shared location, bound to `Location` (and `Venue`) |
| `[State(MyForm.Name)]` | Only while the user is at that step of a form |
| `[PreCheckout]`, `[Paid("code")]` | Telegram Stars payments |

An action can have several routes. A placeholder that only some routes have needs a default value:

```csharp
[Command("catalog")]
[Callback("catalog:{page:int}")]
public IBotResult Catalog(int page = 1) => ...
```

Problems such as a parameter with no source, an unknown placeholder or two actions with the same route stop the bot at startup with a list of every problem.

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

Lists get pagination with one call:

```csharp
message.Page(products, page, pageSize: 5,
    item: p => Button(p.Name, $"product:{p.Id}"),
    route: page => $"catalog:{page}");
```

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

States are kept in memory by default. Replace the storage with `.UseStateStorage<RedisStateStorage>()`.

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
    "Stars": { "PaySupportMessage": "Problems with a purchase? Write to @MyShopSupport." },
    "Logging": { "MinimumLevel": "Information" }
  }
}
```

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

## License

[MIT](https://github.com/VAXA99/TelegramBotForge/blob/main/LICENSE)
