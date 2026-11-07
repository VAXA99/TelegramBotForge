using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Exceptions;
using TelegramBotForge.Configurations;
using TelegramBotForge.Controllers;
using TelegramBotForge.Logging;
using TelegramBotForge.Results;
using TelegramBotForge.Views;

namespace TelegramBotForge.Middleware;

/// <summary>
/// Built-in middleware. Catches exceptions from later middleware and actions, logs them,
/// and answers the user with <see cref="ErrorView"/> ("An error occurred" by default).
/// </summary>
/// <remarks>
/// To change the message, redefine <see cref="ErrorView"/>. To answer some errors differently, derive from this class,
/// override <see cref="OnErrorAsync"/>, and register your class with <c>ReplaceMiddleware&lt;ErrorMiddleware, MyErrorMiddleware&gt;()</c>.
/// </remarks>
/// <example>
/// <code>
/// public sealed class MyErrorMiddleware(ILoggerFactory loggers, IOptions&lt;TelegramBotConfiguration&gt; options)
///     : ErrorMiddleware(loggers, options)
/// {
///     protected override Task&lt;IBotResult?&gt; OnErrorAsync(BotContext context, Exception exception, CancellationToken ct) =>
///         exception is ValidationException validation
///             ? Task.FromResult&lt;IBotResult?&gt;(new SendText(validation.Message))
///             : base.OnErrorAsync(context, exception, ct);
/// }
/// </code>
/// </example>
public class ErrorMiddleware : IBotMiddleware
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ErrorMiddleware"/> class.
    /// </summary>
    /// <param name="loggerFactory">Creates the <c>TelegramBotForge.Errors</c> logger.</param>
    /// <param name="options">The bot configuration.</param>
    public ErrorMiddleware(ILoggerFactory loggerFactory, IOptions<TelegramBotConfiguration>? options = null)
    {
        Logger = loggerFactory.CreateLogger("TelegramBotForge.Errors");
        Options = options?.Value ?? new TelegramBotConfiguration();
    }

    /// <summary>
    /// Gets the logger.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Gets the bot configuration.
    /// </summary>
    protected TelegramBotConfiguration Options { get; }

    /// <inheritdoc/>
    public virtual async Task<IBotResult?> InvokeAsync(BotContext context, BotNext next, CancellationToken ct)
    {
        try
        {
            return await next(context, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            UpdateTrace.MarkFailed();
            return await OnErrorAsync(context, exception, ct);
        }
    }

    /// <summary>
    /// Logs the exception and builds the answer for the user.
    /// </summary>
    /// <param name="context">The update context.</param>
    /// <param name="exception">The exception thrown by a later middleware or the action.</param>
    /// <param name="ct">A token to cancel processing of the update.</param>
    /// <returns>The result sent instead of the action's result, or <c>null</c> for no answer.</returns>
    protected virtual Task<IBotResult?> OnErrorAsync(BotContext context, Exception exception, CancellationToken ct)
    {
        var update = UpdateDescriber.Describe(context.Update, Options.Logging?.IncludeMessageText ?? true);
        var sender = UpdateDescriber.DescribeSender(context.Update);
        var action = UpdateTrace.Current?.HandledBy ?? "no action";

        if (exception is ApiRequestException { ErrorCode: 403 } forbidden)
        {
            // The user blocked the bot or left the chat: nothing can be delivered, so there is nothing to answer.
            Logger.LogWarning("{Update} from {Sender} → {Action}: Telegram refused the request ({Reason})",
                update, sender, action, forbidden.Message);
            return Task.FromResult<IBotResult?>(null);
        }

        Logger.LogError(exception, "{Update} from {Sender} → {Action} failed", update, sender, action);

        if (context.ChatId is null)
            return Task.FromResult<IBotResult?>(null);

        try
        {
            return Task.FromResult(ViewResults.From(context.Services.GetRequiredService<ErrorView>()));
        }
        catch (Exception viewError)
        {
            Logger.LogError(viewError, "The error view failed too, so the user gets no answer");
            return Task.FromResult<IBotResult?>(null);
        }
    }
}
