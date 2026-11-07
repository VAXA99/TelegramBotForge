using Microsoft.Extensions.DependencyInjection;
using TelegramBotForge.Results;

namespace TelegramBotForge.Middleware;

/// <summary>
/// Calls the rest of the pipeline: the next middleware, or the router and the action at the end.
/// </summary>
/// <param name="context">The update context.</param>
/// <param name="ct">A token to cancel processing of the update.</param>
/// <returns>The result of the rest of the pipeline, or <c>null</c> for no answer.</returns>
public delegate Task<IBotResult?> BotNext(BotContext context, CancellationToken ct);

/// <summary>
/// Code that runs around every update, before and after its action, like ASP.NET Core middleware.
/// Use it for checks and bookkeeping that every action would otherwise repeat:
/// banned users, creating the user record on first contact, rate limits, metrics or transactions.
/// </summary>
/// <remarks>
/// Add it with <c>UseMiddleware&lt;T&gt;()</c>. To stop an update, return your own result (or <c>null</c>) without calling <c>next</c>.
/// To pass data to actions, put it in <see cref="BotContext.Items"/>.
/// </remarks>
/// <example>
/// <code>
/// public sealed class EnsureUserMiddleware(IUserRepository users) : IBotMiddleware
/// {
///     public async Task&lt;IBotResult?&gt; InvokeAsync(BotContext context, BotNext next, CancellationToken ct)
///     {
///         context.Items["user"] = await users.GetOrCreateAsync(context.UserId, ct);
///         return await next(context, ct);
///     }
/// }
/// </code>
/// </example>
public interface IBotMiddleware
{
    /// <summary>
    /// Processes the update.
    /// </summary>
    /// <param name="context">The update context.</param>
    /// <param name="next">Calls the rest of the pipeline.</param>
    /// <param name="ct">A token to cancel processing of the update.</param>
    /// <returns>The result to send: the one returned by <paramref name="next"/>, your own, or <c>null</c> for no answer.</returns>
    Task<IBotResult?> InvokeAsync(BotContext context, BotNext next, CancellationToken ct);
}

/// <summary>
/// The middleware chain with the router at its end, composed once at startup.
/// </summary>
internal sealed class BotPipeline
{
    private readonly BotNext _entry;

    public BotPipeline(IReadOnlyList<Type> middlewareTypes, BotNext terminal)
    {
        var next = terminal;
        for (var i = middlewareTypes.Count - 1; i >= 0; i--)
        {
            var type = middlewareTypes[i];
            var inner = next;
            next = (context, ct) =>
                ((IBotMiddleware)context.Services.GetRequiredService(type)).InvokeAsync(context, inner, ct);
        }

        _entry = next;
        MiddlewareTypes = middlewareTypes;
    }

    public IReadOnlyList<Type> MiddlewareTypes { get; }

    public Task<IBotResult?> InvokeAsync(BotContext context, CancellationToken ct) => _entry(context, ct);
}
