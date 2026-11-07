using Telegram.Bot.Types;
using TelegramBotForge.Logging;
using TelegramBotForge.Results;

namespace TelegramBotForge.Middleware;

/// <summary>
/// Built-in middleware. Ignores updates sent by other bots,
/// so two bots in one chat cannot answer each other forever.
/// </summary>
/// <remarks>
/// To let some bot updates through, derive from it, override <see cref="ShouldIgnore"/>, and register your class with
/// <c>ReplaceMiddleware&lt;BotMessageFilterMiddleware, MyFilter&gt;()</c>. To let all through, use <c>RemoveMiddleware</c>.
/// </remarks>
public class BotMessageFilterMiddleware : IBotMiddleware
{
    /// <inheritdoc/>
    public virtual Task<IBotResult?> InvokeAsync(BotContext context, BotNext next, CancellationToken ct)
    {
        if (!ShouldIgnore(context.Update))
            return next(context, ct);

        UpdateTrace.Mark("ignored (sent by a bot)");
        return Task.FromResult<IBotResult?>(null);
    }

    /// <summary>
    /// Decides whether an update is ignored.
    /// </summary>
    /// <param name="update">The update.</param>
    /// <returns><c>true</c> to stop the update.</returns>
    protected virtual bool ShouldIgnore(Update update) =>
        UpdateDescriber.GetSender(update) is { IsBot: true };
}
