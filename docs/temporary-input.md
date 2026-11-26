# Scoped context and temporary input

## Current context through DI

`BotContext` is registered as scoped. Inject it directly into scoped services or middleware:

```csharp
public sealed class ProfileSessionService(BotContext context)
{
    public void SetMessageId(int id) => context.State!.Set("profile.messageId", id);
}

// In application registration:
builder.Services.AddScoped<ProfileSessionService>();
```

It is the same instance used by middleware, controllers, delivery callbacks and finalizers, bound before constructing
user middleware. There is no public accessor, global mutable context or application initialization middleware.
State is loaded by the existing state middleware before routing: read `context.State` when needed, not in a middleware
constructor. Parallel updates have independent scopes. Do not capture context in singleton services or use it after
the operation ends. An unrelated scope cannot resolve it: an `InvalidOperationException` explains that an update or
background-delivery operation is required. Root resolution may instead be rejected by the container's scope validator.

`IBotMessageSender` and legacy broadcasts bind their own delivery context before resolving the executor and scoped
services. It has the target chat and cancellation token, but no invented `From`, `UserId` or `State`.
Temporary conversation input therefore cannot be started from background delivery.

## Typed users selection

`[UsersShared]` matches only new messages containing `Message.UsersShared`. A `Telegram.Bot.Types.UsersShared` action
parameter and `BotContext.UsersShared` expose the original object, including request ID and all requested user fields:

```csharp
[UsersShared, State("select-users"), ChatType(ChatType.Private)]
public Task<IBotResult?> SelectedUsers(UsersShared selection, CancellationToken ct) =>
    HandleAsync(selection.RequestId, selection.Users, ct);
```

Action filters and chat constraints apply normally. Priority is 20, like contacts/locations, with the usual +50 for
a state constraint. Text, contacts, locations, edits and unrelated messages do not match. Outgoing buttons still use
`Message.RequestUsers` and `KeyboardButtonRequestUsers`. Business validation belongs to the application.

## Temporary input

`ITemporaryInput` is an automatically registered scoped service backed by `ChatState`. It provides separate `Begin`,
`TryMatch`, `Complete` and `Cancel` operations. Text, authorization, validation and business effects remain application code:

```csharp
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Controllers;
using TelegramBotForge.Input;
using TelegramBotForge.Results;

public sealed class SelectionController(ITemporaryInput input, SelectionView view) : BotController
{
    [Command("select")]
    public IBotResult Open() => input.Begin("select-users", TemporaryInputKind.Users,
        request => view.Prompt().RequestUsers("Select users",
            new KeyboardButtonRequestUsers(request.RequestId!.Value) { MaxQuantity = 5 }),
        new TemporaryInputOptions(TimeSpan.FromMinutes(5), "Selection expired."));

    [State("select-users"), UsersShared]
    public IBotResult Selected(UsersShared selection)
    {
        if (!input.TryMatch(out var request))
            return new MultiResult();
        // Apply application validation before completing.
        State.Set("selectedUsers", selection.Users.Select(user => user.UserId).ToArray());
        return input.Complete(request.Id, "Selection saved.");
    }

    [Callback("selection-cancel:{id}")]
    public IBotResult Cancel(Guid id) => input.Cancel(id, "Selection cancelled.");
}
```

The view returns a standard-mode message: reply keyboards cannot be combined with rich messages or inline buttons on
the same message. The prompt factory receives a unique opening `Id` for optional cancellation UI in a separate message
and an automatically allocated Telegram `RequestId`. Always use that request ID on `RequestUsers`.
For location prompts use `TemporaryInputKind.Location` and `.RequestLocation("Share location")`; `RequestId` is null.

### Correlation and state

- Beginning preserves the previous `ChatState.Current` and **all existing scenario data**, then enters the temporary
  state. The pending `Current.Prompt` is null until confirmed `OnDelivered`. Return and execute the result from `Begin`;
  merely calling it does not send anything.
- The entry stores primary/associated output IDs and its deadline. Lifetime starts at confirmation. `TryMatch` requires
  the same chat, user, temporary state, confirmed delivery and an unexpired, correlated answer.
- Users selection must carry the allocated request ID. A retained conversation sequence avoids reuse by later requests.
  After storage expiry/reset, a new sequence is randomly seeded; absolute deduplication across lost storage is not promised.
- Locations have **no Telegram request ID**. To reject old-window responses reliably, locations must explicitly reply
  to the current prompt (`ReplyToMessage.Id`). An uncorrelated `RequestLocation` response is rejected, never guessed to
  belong to the latest opening. Tell users to reply to the prompt; if their client cannot, use another correlated input.
- `Complete` checks correlation again; `Cancel` requires ownership and the opening's `Guid`, but no answer. Both remove
  the entry and restore only the previous state name, retaining scenario data. Repeated/stale closes do nothing,
  including no removal of a newer keyboard. Pass the closing window's original ID, not the latest `Current.Id`.
- A second active request throws instead of replacing the first. To replace explicitly, cancel the old opening, begin
  the new one, then return `cancelResult.Then(beginResult)` so old keyboard cleanup precedes the new prompt.

### Failure, interruption and expiry

Successful action changes are saved before delivery, as before. Prompt delivery failure/cancellation rolls back pending
state in `Finally`; final persistence saves rollback and outer middleware finalizers using a non-cancelled token.
A confirmed receipt stays active even if later callbacks/finalizers fail or cancel. `MessageDeliveryException` preserves
that receipt; never resend a confirmed prompt. Failed actions do not save their changes. A pending entry left by a process
interruption/discarded result is restored on the next successful update, never accepted as successfully opened.

Closing restores state **before** cleanup. Cleanup deletes the prompt and associated media and sends the application's
plain-text notice with `ReplyKeyboardRemove`, using an independent cancellation token. Failures are logged, best effort,
without automatic retries: they do not reopen the request, re-execute business actions or retry confirmed deliveries.
Telegram reply keyboards belong to the chat: avoid overlapping reply-keyboard flows in groups even though request
ownership/state are isolated per chat/user.

Prompt deletion additionally uses the existing in-memory `DeleteAfter` scheduler. On restart its timer is lost, but stored
receipt/deadline remain. Before routing the next conversation update, state middleware clears expired input, restores
the previous state and prepends cleanup **before** the action's result, never after a new keyboard. No update means
state/keyboard cleanup is lazy; `TryMatch` always rejects expired answers. Failed actions do not commit pre-routing cleanup;
the next successful update tries again. If the application independently changed the current state, the abandoned entry
and prompt are removed without overwriting that state or removing its keyboard.

Commands preserve input by default. Pass `TemporaryInputCommandPolicy.Cancel` to opt into closing before a command for
this bot is routed; commands for other bots do not interrupt. Identity, ownership and deadline checks are unconditional.

The library reserves `TelegramBotForge.Input.Active` and `TelegramBotForge.Input.Sequence` state keys. Do not edit them;
application keys remain untouched. Lifetime must be positive, fit `DeleteAfter` limits and not exceed `KeepStateFor`.
Use durable storage for restart recovery and a TTL beyond the desired lifetime. Whole-state clearing, storage loss and
business-side transaction failures remain application concerns: this is not a workflow engine or durable exactly-once
business transaction manager.
