using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelegramBotForge.Configurations;
using TelegramBotForge.Controllers;
using TelegramBotForge.Logging;
using TelegramBotForge.Results;
using TelegramBotForge.Views;

namespace TelegramBotForge.Middleware;

/// <summary>
/// Built-in middleware, off by default. Limits how many updates one user can send in a time window
/// (<see cref="TelegramBotConfiguration.RateLimit"/>). The first update over the limit gets <see cref="RateLimitView"/>;
/// the rest of the window is ignored silently, and pressed buttons still stop their loading spinner.
/// </summary>
/// <remarks>
/// Turn it on with <c>"RateLimit": { "Enabled": true }</c>. Redefine <see cref="RateLimitView"/> to change the message,
/// override <see cref="OnLimitedAsync"/> to answer differently, or <see cref="GetLimitKey"/> to limit per chat instead of per user.
/// It is registered as a singleton: the time windows are kept in this instance.
/// </remarks>
public class RateLimitMiddleware : IBotMiddleware
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<long, Window> _windows = new();
    private readonly TimeProvider _time;
    private long _nextSweepTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitMiddleware"/> class.
    /// </summary>
    /// <param name="loggerFactory">Creates the <c>TelegramBotForge.RateLimit</c> logger.</param>
    /// <param name="options">The bot configuration; its <see cref="TelegramBotConfiguration.RateLimit"/> section is used.</param>
    /// <param name="timeProvider">The clock. Defaults to the system clock.</param>
    public RateLimitMiddleware(
        ILoggerFactory loggerFactory,
        IOptions<TelegramBotConfiguration>? options = null,
        TimeProvider? timeProvider = null)
    {
        Logger = loggerFactory.CreateLogger("TelegramBotForge.RateLimit");
        Options = options?.Value.RateLimit ?? new TelegramBotRateLimitOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the logger.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Gets the rate limit settings.
    /// </summary>
    protected TelegramBotRateLimitOptions Options { get; }

    /// <inheritdoc/>
    public virtual async Task<IBotResult?> InvokeAsync(BotContext context, BotNext next, CancellationToken ct)
    {
        var userId = UpdateDescriber.GetSender(context.Update)?.Id ?? 0;
        if (!Options.Enabled || Options.ExemptUserIds.Contains(userId) || IsPayment(context) || IsExempt(context))
            return await next(context, ct);

        var key = GetLimitKey(context);
        if (key == 0)
            return await next(context, ct);

        var now = _time.GetUtcNow();
        var window = _windows.GetOrAdd(key, _ => new Window());
        bool limited, firstTime = false;

        lock (window)
        {
            var start = now - TimeSpan.FromSeconds(Options.PerSeconds);
            while (window.Hits.Count > 0 && window.Hits.Peek() <= start)
                window.Hits.Dequeue();

            limited = window.Hits.Count >= Options.MaxUpdates;
            if (limited)
            {
                firstTime = !window.Warned;
                window.Warned = true;
            }
            else
            {
                window.Hits.Enqueue(now);
                window.Warned = false;
            }

            window.LastSeen = now;
        }

        SweepIfDue(now);

        if (!limited)
            return await next(context, ct);

        UpdateTrace.Mark("rate limited");
        return await OnLimitedAsync(context, firstTime, ct);
    }

    /// <summary>
    /// Gets the key the limit applies to. The default is the user id; return 0 to never limit an update.
    /// </summary>
    /// <param name="context">The update context.</param>
    protected virtual long GetLimitKey(BotContext context) =>
        UpdateDescriber.GetSender(context.Update)?.Id ?? 0;

    /// <summary>
    /// Builds the answer to an update over the limit.
    /// </summary>
    /// <param name="context">The update context.</param>
    /// <param name="firstTimeInWindow"><c>true</c> for the first limited update in the window, when the user should be told.</param>
    /// <param name="ct">A token to cancel processing of the update.</param>
    /// <returns>The result sent instead of running the action.</returns>
    protected virtual Task<IBotResult?> OnLimitedAsync(BotContext context, bool firstTimeInWindow, CancellationToken ct)
    {
        if (firstTimeInWindow)
        {
            Logger.LogWarning("{Sender} is rate limited: more than {MaxUpdates} updates in {PerSeconds}s",
                UpdateDescriber.DescribeSender(context.Update), Options.MaxUpdates, Options.PerSeconds);
        }

        var view = firstTimeInWindow ? context.Services.GetRequiredService<RateLimitView>() : null;

        // A pressed button is always answered, so its loading spinner stops; the message becomes its notification.
        if (context.CallbackQuery is not null)
            return Task.FromResult<IBotResult?>(new AnswerCallback(view?.RenderPlainText()));

        return Task.FromResult(view is null || context.ChatId is null ? null : ViewResults.From(view));
    }

    /// <summary>
    /// Payments are never limited: a limited check before payment would fail the payment, and a paid order must be delivered.
    /// </summary>
    // Inline queries arrive while the user types, and membership changes are not messages; neither should use up the limit.
    private static bool IsExempt(BotContext context) =>
        context.InlineQuery is not null || context.MembershipChange is not null;

    private static bool IsPayment(BotContext context) =>
        context.Update.PreCheckoutQuery is not null || context.Message?.SuccessfulPayment is not null;

    private void SweepIfDue(DateTimeOffset now)
    {
        var next = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < next ||
            Interlocked.CompareExchange(ref _nextSweepTicks, (now + SweepInterval).UtcTicks, next) != next)
        {
            return;
        }

        var expired = now - TimeSpan.FromSeconds(Options.PerSeconds);
        foreach (var pair in _windows)
        {
            if (pair.Value.LastSeen <= expired)
                _windows.TryRemove(pair);
        }
    }

    private sealed class Window
    {
        public Queue<DateTimeOffset> Hits { get; } = new();

        public bool Warned { get; set; }

        public DateTimeOffset LastSeen { get; set; }
    }
}
