# Scoped Stars providers and incoming media sources

## Stars provider lifetimes

Keep product repositories and their database dependencies scoped. The provider participates in the **existing update
scope** and needs no `IServiceScopeFactory` or manually created scope:

```csharp
using Microsoft.Extensions.DependencyInjection;
using TelegramBotForge.Payments;

hostBuilder.Services.AddScoped<IProductRepository, ProductRepository>();
hostBuilder.Services.AddTelegramBot(hostBuilder.Configuration)
    .AddStars()
    .UseStarsProducts<DatabaseStarsProducts>(ServiceLifetime.Scoped);

public sealed class DatabaseStarsProducts(IProductRepository repository) : IStarsProducts
{
    public async ValueTask<StarsProduct?> FindAsync(string code, CancellationToken ct) =>
        await repository.FindAsync(code, ct);
}
```

`IProductRepository` and `ProductRepository` are application-defined examples, not additional library APIs. Your provider
may also inject other scoped services, including localization or a database context. The current operation's culture
and cancellation token apply to `FindAsync`; scoped dependencies are disposed by the operation, including on errors or
cancellation, after result execution and its finalizers. Do not dispose a DI-owned provider in your action/middleware.

| Registration | Behavior |
|---|---|
| `UseStarsProducts<T>()` | Existing singleton behavior; the same provider across operations. Dependencies must be singleton-compatible. |
| `UseStarsProducts<T>(ServiceLifetime.Singleton)` | Explicit form of the same behavior. |
| `UseStarsProducts<T>(ServiceLifetime.Scoped)` | Same provider and scoped dependencies for all resolutions in one operation; different operations are isolated. |
| `UseStarsProducts<T>(ServiceLifetime.Transient)` | New provider per DI resolution, with dependencies taken from the current operation scope. Disposable instances are owned by that scope. No per-update provider identity guarantee. |

Undefined `ServiceLifetime` values throw `ArgumentOutOfRangeException` immediately. The original overload remains, rather
than being replaced by an optional parameter, preserving existing call sites and its compiled signature.

The library's `StarsMiddleware` is now **scoped**, retaining its public constructor and never capturing an operation's
provider in a library singleton. Invoice execution already resolves `IStarsProducts` from `BotContext.Services`;
pre-checkout and successful payment use the middleware's operation-scoped provider. These are separate Telegram updates
and therefore separate scopes. Repeated resolution within a scoped operation returns the same object, but every
`FindAsync` remains a fresh provider call: the library introduces no global product/price cache.

Pre-checkout still reads the current price and declines removed products or changed prices. A successful payment still
reaches its payment action with `StarsPayment.Product == null` if the product subsequently disappears. Existing duplicate
charge protection, provider exception behavior, result lifecycle and final state persistence remain unchanged.

Startup never resolves scoped or transient product providers from the root container, and does not create a temporary
scope to inspect them. Static singleton `StarsCatalog` validation still checks product codes and paid routes. Dynamic
providers cannot be enumerated at startup; general payment-route and `/paysupport` checks still apply. Singleton providers
retain their existing startup resolution behavior. Do not resolve a scoped provider or the scoped `StarsMiddleware`
from the root provider; resolve them only in an operation (or use normal constructor injection in scoped services).

## Media source IDs

`BotMedia.SourceMessageId` is the actual `Telegram.Bot.Types.Message.Id` of the source. `BotMedia.From` populates it for
photos, animations, videos, documents, audio, voice, video notes and stickers. `BotContext.Media`, action-bound media and
every `BotContext.Album` element retain that ID. `BotContext.AlbumMessages` remains available for the complete original
Telegram objects; source matching no longer requires joining those collections by index.

For example, application-controlled acceptance/deletion in an album action:

```csharp
using TelegramBotForge.Controllers;
using TelegramBotForge.Media;
using TelegramBotForge.Results;

public sealed class UploadController(IMediaStore store) : BotController
{
    [Album]
    public async Task<IBotResult> Upload(IReadOnlyList<BotMedia> items, CancellationToken ct)
    {
        var results = new List<IBotResult>();
        foreach (var media in items)
        {
            if (await store.SaveAsync(media, ct) && media.SourceMessageId is { } messageId)
                results.Add(new DeleteMessage(messageId));
        }
        return new MultiResult(results.ToArray());
    }
}
```

`IMediaStore` and acceptance/save rules belong to the application; the library does not save or delete incoming media
automatically. Filtering or reordering `items` does not change the relationship. Identical `FileId`/`FileUniqueId` values
in multiple messages still retain their individual message IDs. Existing album collection, sorting, routing and largest
photo-size selection are unchanged. A source ID identifies a message **only in the source update's chat**, not across
chats; retain that chat ID too if processing media later or elsewhere.

### Compatibility

`SourceMessageId` is a non-positional `int?` init property. The original eleven-argument `BotMedia` constructor and
eleven-value `Deconstruct` are unchanged. Manual construction leaves it `null` unless explicitly set; there is no
placeholder zero or inferred album index. Copying with `with` preserves it unless explicitly replaced.

As with other record properties, **record equality and hash codes now include `SourceMessageId`**. Two otherwise identical
items from different source messages are no longer equal; source-less manual objects retain their previous equality
behavior. If application logic needs file-only deduplication, compare the desired file fields explicitly instead of
using whole-record equality. Source IDs do not include a chat ID, so whole-record equality is not global message identity.
