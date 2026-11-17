using Microsoft.Extensions.DependencyInjection;

namespace TelegramBotForge.Results;

public static class ResultLifecycleExtensions
{
    /// <summary>Observes each confirmed receipt in result order, before enclosing result finalizers. No callback runs for undelivered output.</summary>
    public static IBotResult OnDelivered(this IBotResult result, Func<MessageDelivery, CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(callback);
        return new DeliveredResult(result, callback);
    }

    /// <summary>Runs once after execution, including failure and cancellation, with a non-cancelled token.</summary>
    public static IBotResult Finally(this IBotResult result, Func<CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(callback);
        return new FinishedResult(result, callback);
    }

    public static IBotResult Finally(this IBotResult result, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return result.Finally(_ => { callback(); return Task.CompletedTask; });
    }

    public static IBotResult Finally(this IBotResult result, Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return result.Finally(_ => callback());
    }

    /// <summary>Schedules deletion of all deliveries, even when a later callback fails. In-memory until host shutdown.</summary>
    public static IBotResult DeleteAfter(this IBotResult result, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidateDelay(delay);
        return new ExpiringResult(result, delay);
    }

    internal static void ValidateDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero || delay.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(delay), "Deletion delay must be between zero and 4294967294 milliseconds.");
    }

    internal static async Task FinishAsync(Func<Task> execute, Func<CancellationToken, Task> callback, BotContext? context = null)
    {
        var firstDelivery = context?.DeliveredMessages.Count ?? 0;
        Exception? failure = null;
        try { await execute(); }
        catch (Exception exception) { failure = exception; throw; }
        finally
        {
            try { await callback(CancellationToken.None); }
            catch (Exception exception)
            {
                if (failure is MessageDeliveryException delivered)
                    throw new MessageDeliveryException(delivered.Delivery, [failure, exception]);
                if (failure is not null)
                    throw new AggregateException("Result execution and finalization failed.", failure, exception);
                if (context is not null && context.DeliveredMessages.Count > firstDelivery)
                    throw new MessageDeliveryException(context.DeliveredMessages[^1], [exception]);
                throw;
            }
        }
    }

    internal static async Task WithDeliveryAsync(BotContext context, Func<Task> execute)
    {
        var firstDelivery = context.DeliveredMessages.Count;
        try { await execute(); }
        catch (MessageDeliveryException) { throw; }
        catch (Exception exception) when (context.DeliveredMessages.Count > firstDelivery)
        {
            throw new MessageDeliveryException(context.DeliveredMessages[^1], [exception]);
        }
    }

    internal static async Task ExecuteAsync(BotContext context, IBotResult result,
        Func<BotContext, IBotResult, CancellationToken, Task> execute, CancellationToken ct,
        IReadOnlyList<Func<MessageDelivery, CancellationToken, Task>>? deliveredCallbacks = null)
    {
        switch (result)
        {
            case DeliveredResult delivered:
                await ExecuteAsync(context, delivered.Inner, execute, ct, [delivered.Callback, .. (deliveredCallbacks ?? [])]);
                break;
            case FinishedResult finished:
                await FinishAsync(() => ExecuteAsync(context, finished.Inner, execute, ct, deliveredCallbacks), finished.Callback, context);
                break;
            case ExpiringResult expiring:
                await ExpireAsync(context, expiring.Delay, () => ExecuteAsync(context, expiring.Inner, execute, ct, deliveredCallbacks));
                break;
            case MultiResult multi:
                foreach (var nested in multi.Results)
                    await ExecuteAsync(context, nested, execute, ct, deliveredCallbacks);
                break;
            default:
                if (deliveredCallbacks is null)
                    await execute(context, result, ct);
                else
                    await NotifyDeliveredAsync(context, () => execute(context, result, ct), deliveredCallbacks, ct);
                break;
        }
    }

    private static async Task NotifyDeliveredAsync(BotContext context, Func<Task> execute,
        IReadOnlyList<Func<MessageDelivery, CancellationToken, Task>> callbacks, CancellationToken ct)
    {
        var firstDelivery = context.DeliveredMessages.Count;
        Exception? failure = null;
        try { await execute(); }
        catch (Exception exception) { failure = exception; }

        foreach (var receipt in context.DeliveredMessages.Skip(firstDelivery).ToArray())
        {
            var errors = new List<Exception>();
            foreach (var callback in callbacks)
            {
                try { await callback(receipt, ct); }
                catch (Exception exception) { errors.Add(exception); }
            }
            if (errors.Count == 0)
                continue;
            if (failure is not null)
                errors.Insert(0, failure);
            failure = new MessageDeliveryException(receipt, errors);
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static async Task ExpireAsync(BotContext context, TimeSpan delay, Func<Task> execute)
    {
        var scheduler = context.Services.GetRequiredService<MessageDeletionScheduler>();
        var previous = context.DeliveryCommitted;
        context.DeliveryCommitted = receipt => scheduler.Schedule(receipt, delay);
        try { await execute(); }
        finally { context.DeliveryCommitted = previous; }
    }
}

internal sealed record FinishedResult(IBotResult Inner, Func<CancellationToken, Task> Callback) : IBotResult;
internal sealed record DeliveredResult(IBotResult Inner, Func<MessageDelivery, CancellationToken, Task> Callback) : IBotResult;
internal sealed record ExpiringResult(IBotResult Inner, TimeSpan Delay) : IBotResult;
