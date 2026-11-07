using System.Collections.Concurrent;
using Telegram.Bot;

namespace TelegramBotForge.Payments;

/// <summary>
/// Remembers which payments were processed, so the same payment is never handled twice,
/// for example when Telegram delivers the update again after a webhook error.
/// The default keeps them in memory for 30 days; register your own with <c>UseProcessedPayments&lt;T&gt;()</c>
/// to survive restarts.
/// </summary>
public interface IProcessedPayments
{
    /// <summary>
    /// Records a payment. Returns <c>false</c> when it was already recorded.
    /// </summary>
    ValueTask<bool> TryAddAsync(string chargeId, CancellationToken ct);

    /// <summary>
    /// Forgets a payment, so it can be processed again. Called when its action failed.
    /// </summary>
    ValueTask RemoveAsync(string chargeId, CancellationToken ct);
}

/// <summary>
/// Keeps processed payments in memory for 30 days. Lost on restart.
/// </summary>
public sealed class InMemoryProcessedPayments : IProcessedPayments
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _payments = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private DateTimeOffset _nextSweep;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryProcessedPayments"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock. Defaults to the system clock.</param>
    public InMemoryProcessedPayments(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _nextSweep = _time.GetUtcNow() + SweepInterval;
    }

    /// <inheritdoc/>
    public ValueTask<bool> TryAddAsync(string chargeId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (now >= _nextSweep)
        {
            _nextSweep = now + SweepInterval;
            foreach (var pair in _payments)
            {
                if (now - pair.Value > KeepFor)
                    _payments.TryRemove(pair);
            }
        }

        return ValueTask.FromResult(_payments.TryAdd(chargeId, now));
    }

    /// <inheritdoc/>
    public ValueTask RemoveAsync(string chargeId, CancellationToken ct)
    {
        _payments.TryRemove(chargeId, out _);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Refunds Stars payments outside an action, for example from an admin tool. Inside an action, return <c>Refund(chargeId)</c>.
/// </summary>
public interface IStarsPayments
{
    /// <summary>
    /// Gives the Stars of a payment back to the user.
    /// </summary>
    /// <param name="userId">The buyer.</param>
    /// <param name="chargeId">The <see cref="StarsPayment.ChargeId"/> of the payment.</param>
    /// <param name="ct">Cancels the request.</param>
    Task RefundAsync(long userId, string chargeId, CancellationToken ct = default);
}

internal sealed class TelegramStarsPayments : IStarsPayments
{
    private readonly ITelegramBotClient _bot;

    public TelegramStarsPayments(ITelegramBotClient bot) => _bot = bot;

    public Task RefundAsync(long userId, string chargeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chargeId);
        return _bot.RefundStarPayment(userId: userId, telegramPaymentChargeId: chargeId, cancellationToken: ct);
    }
}
