# Changelog

All notable changes to TelegramBotForge. The public API of each package is listed in its `PublicAPI.Shipped.txt`.

## Unreleased

- Keep the selected update culture through result execution, delivery/finalization callbacks, final state persistence and scope disposal, restoring it on success and failure.
- Add `IBotResult.OnDelivered` for actual receipts from sends, edits and replacements; sequences notify per confirmed receipt in order.
- Add structured and legacy `RequestUsers` buttons with complete option snapshots and existing reply-keyboard validation.

- Confirmed deliveries now expose post-delivery failures as `MessageDeliveryException` with the receipt and original errors.
- Broadcasts report callback, finalizer, cleanup and `OnSent` errors in `PostDeliveryErrors`, counting delivered chats only in `Sent` and excluding them from retries even after cancellation.
- Final state persistence runs in the update dispatcher after all result finalizers, including those added by outer middleware, using a token independent of delivery cancellation.

- Public `IBotMessageSender` for structured background sends/edits; `IBotBroadcaster` gains `Message` and per-chat factory overloads using that sender.
- `Finally` on results and messages, including cancellation; receipt-based `DeleteAfter` for primary and associated media through a hosted in-memory scheduler.
- State changes in delivery/finalization callbacks are saved after execution, including failed/cancelled deliveries; action failure behavior is unchanged.
- Automatic bot-identity deep-link building (`start` / `startgroup`), immediate payload validation, typed `[DeepLink]` routes and startup checks.
- Canonical 22-character base64url GUID helpers (`ShortId`) with typed GUID binding in deep links and callbacks.

- Annotated, immutable message factories: public screen methods return `Views.Message` directly; `View` requires no override.
- Cached method/view mode resolution with `[Rich]` and `[Standard]`, inherited annotations, and startup ambiguity checks.
- Escaped inline content, headings, tables, details, components, ordered button rows and registered photo/video slideshows.
- Structured standard/rich delivery through `Reply`/`Edit`, actual delivery receipts, associated media cleanup and callbacks.
- Explicit `RawHtml` insertion with mode-aware structure, length, reference and keyboard validation.
- Existing callback/URL/copy factories now serialize all three actions in rich messages; composed labels support text and emoji.
- Compatibility: `View<T>`, `Build`/`Render`, `MessageBuilder`, `BotMessage`, escaped legacy interpolation and result factories remain.
- Source compatibility: importing both message namespaces may require a `Message` alias; `[View]` now inherits mode and lifetime.
- Edit errors: only known missing/uneditable targets trigger replacement. Invalid payloads and unrelated 400 errors propagate.
- Corrected the SDK feature-band version in `global.json` so repository builds can run.
- See [annotated views and migration](docs/views.md). Public API additions/removals are recorded in `PublicAPI.Unshipped.txt`.

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
- `Page(items, page, pageSize, renderPage, route, configure)`: a page of a list with any content (text, several buttons
  per item, a table in rich views), with the pagination row after it. `PageInfo` now has `TotalItems`, `PageSize`,
  `FirstItemNumber` and `LastItemNumber`. The existing `Page(..., item, ...)` works as before.

### Changed

- Inline queries and membership changes don't count against the rate limit.
- The `Message` parameter of an action also receives an edited message.
- `BotTestHost` sets `Stars:PaySupportMessage` for you.

## 0.1.0

- First preview: controllers with attribute routes, views, results, middleware, conversation state, Telegram Stars payments,
  long polling and webhooks.
