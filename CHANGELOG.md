# Changelog

All notable changes to TelegramBotForge. The public API of each package is listed in its `PublicAPI.Shipped.txt`.

## 0.2.0

### New packages

- **TelegramBotForge.Testing**: `BotTestHost` runs a bot against a fake Telegram client. A `TestChat` sends text, contacts,
  locations, photos and albums, edits messages, presses buttons, types inline queries, pays invoices, and blocks or unblocks the bot.

### Added

- Targets .NET 10 as well as .NET 8.
- **Flood control** for outgoing messages: limits per chat and overall, with retries after `429 Too Many Requests`
  (`FloodControl` in the configuration, on by default).
- **Command arguments**: `string args` / `string[] args` after a command, which is also how deep links arrive.
  Commands for another bot (`/start@OtherBot`) are ignored.
- **The `/` menu**: `RegisterCommands()` sets it from `[Command(Description = ...)]`, per chat type and language.
- **Filters**: `IBotActionFilter`, `BotFilterAttribute`, `[UseFilter<T>]`, `AddFilter<T>()` for every action, and constraints
  such as `[ChatType]` (`BotConstraintAttribute`).
- **Localization**: `UseLocalization()` takes the language of the user's Telegram app, or yours from `IBotCultureResolver`,
  and sets `CultureInfo.CurrentUICulture` for the update.
- **More routes**: `[ButtonText]`, `[Regex]`, `[Contact]`, `[Edited]`, `[EditedText]`, `[InlineQuery]` with `AnswerInline`
  and `InlineResults.Article`, `[Album]`, `[BotBlocked]`, `[BotUnblocked]`, `[AddedToChat]`, `[RemovedFromChat]`.
- **Your own routes**: derive from `RouteAttribute`.
- **Broadcasts**: `IBotBroadcaster` sends to many users at a safe rate and reports who is unreachable.
- **Monitoring**: metrics and traces (`BotTelemetry`, meter and activity source `TelegramBotForge`), and a health check
  in TelegramBotForge.AspNetCore (`AddHealthChecks().AddTelegramBot()`).
- **Several instances**: `UseConversationLock<T>()` keeps the updates of one conversation in order across instances.
- `DelegatingBotClient`, a base class to wrap the Telegram client.
- Actions can take registered services as parameters, optionally marked `[FromServices]`.
- A warning, logged once, when `Answer(...)` can't be shown because buttons are answered on arrival.
- Each package lists its public API in `PublicAPI.Shipped.txt`, checked on every build.

### Changed

- Inline queries and membership changes don't count against the rate limit.
- The `Message` parameter of an action also receives an edited message.
- `BotTestHost` sets `Stars:PaySupportMessage` for you.

## 0.1.0

- First preview: controllers with attribute routes, views, results, middleware, conversation state, Telegram Stars payments,
  long polling and webhooks.
