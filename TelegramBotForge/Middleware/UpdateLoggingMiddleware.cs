using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelegramBotForge.Configurations;
using TelegramBotForge.Logging;
using TelegramBotForge.Results;

namespace TelegramBotForge.Middleware;

/// <summary>
/// Built-in middleware, first in the pipeline. Writes one log line per update
/// (what arrived, from whom, which action ran, what it answered and how long it took), warns about slow updates,
/// and adds the chat and user to every log line written while the update is processed.
/// </summary>
/// <remarks>
/// Derive from it, override <see cref="LogUpdate"/> to change the line, and register your class with
/// <c>ReplaceMiddleware&lt;UpdateLoggingMiddleware, MyLogging&gt;()</c>.
/// </remarks>
public class UpdateLoggingMiddleware : IBotMiddleware
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateLoggingMiddleware"/> class.
    /// </summary>
    /// <param name="loggerFactory">Creates the <c>TelegramBotForge.Updates</c> logger.</param>
    /// <param name="options">The bot configuration; its <see cref="TelegramBotConfiguration.Logging"/> section is used.</param>
    public UpdateLoggingMiddleware(ILoggerFactory loggerFactory, IOptions<TelegramBotConfiguration>? options = null)
    {
        Logger = loggerFactory.CreateLogger("TelegramBotForge.Updates");
        Options = options?.Value.Logging ?? new TelegramBotLoggingOptions();
    }

    /// <summary>
    /// Gets the logger.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Gets the logging settings.
    /// </summary>
    protected TelegramBotLoggingOptions Options { get; }

    /// <inheritdoc/>
    public virtual async Task<IBotResult?> InvokeAsync(BotContext context, BotNext next, CancellationToken ct)
    {
        var trace = UpdateTrace.Begin();
        var stopwatch = Stopwatch.StartNew();
        var chatId = UpdateDescriber.GetChat(context.Update)?.Id ?? 0;
        var userId = UpdateDescriber.GetSender(context.Update)?.Id ?? 0;
        IBotResult? result = null;

        try
        {
            using (Logger.BeginScope(new UpdateLogScope(chatId, userId)))
            {
                result = await next(context, ct);
                return result;
            }
        }
        finally
        {
            LogUpdate(context, trace.Describe(), result, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Writes the line for a processed update, or a warning when it took longer than <see cref="TelegramBotLoggingOptions.SlowUpdateWarningMs"/>.
    /// </summary>
    /// <param name="context">The update context.</param>
    /// <param name="action">The action that ran, for example <c>ShopController.Buy</c> or <c>no action</c>, and whether it failed.</param>
    /// <param name="result">What the pipeline answered, or <c>null</c> for no answer.</param>
    /// <param name="elapsed">How long the pipeline took. Sending the answer comes after it and is not included.</param>
    protected virtual void LogUpdate(BotContext context, string action, IBotResult? result, TimeSpan elapsed)
    {
        var elapsedMs = (long)elapsed.TotalMilliseconds;
        var update = UpdateDescriber.Describe(context.Update, Options.IncludeMessageText);
        var sender = UpdateDescriber.DescribeSender(context.Update);
        var answer = UpdateDescriber.DescribeResult(result);

        if (Options.SlowUpdateWarningMs > 0 && elapsedMs >= Options.SlowUpdateWarningMs)
        {
            Logger.LogWarning(
                "Slow update: {Update} from {Sender} → {Action} → {Result} took {ElapsedMs} ms",
                update, sender, action, answer, elapsedMs);
            return;
        }

        var level = Options.LogUpdates ? LogLevel.Information : LogLevel.Debug;
        if (Logger.IsEnabled(level))
        {
            Logger.Log(level, "{Update} from {Sender} → {Action} → {Result} ({ElapsedMs} ms)",
                update, sender, action, answer, elapsedMs);
        }
    }
}
