namespace TelegramBotForge.Hosting;

/// <summary>
/// Makes sure only one process at a time handles a conversation, when the bot runs as several instances.
/// </summary>
/// <remarks>
/// Inside one process the queues already keep the updates of a conversation in order. With webhooks behind a load
/// balancer, two instances can get updates of the same chat at the same moment: answers can arrive out of order, and the
/// conversation state written by one can be overwritten by the other. A distributed lock, for example in Redis, around
/// each update prevents that. Register it with <c>UseConversationLock&lt;T&gt;()</c>. Checks before payment do not take
/// the lock, because Telegram gives them only 10 seconds.
/// </remarks>
/// <example>
/// <code>
/// public sealed class RedisConversationLock(IConnectionMultiplexer redis) : IConversationLock
/// {
///     public async ValueTask&lt;IAsyncDisposable&gt; AcquireAsync(string key, CancellationToken ct)
///     {
///         var db = redis.GetDatabase();
///         var token = Guid.NewGuid().ToString("N");
///         while (!await db.LockTakeAsync("bot:lock:" + key, token, TimeSpan.FromSeconds(30)))
///             await Task.Delay(20, ct);
///         return new Release(db, "bot:lock:" + key, token);
///     }
///
///     private sealed record Release(IDatabase Db, string Key, string Token) : IAsyncDisposable
///     {
///         public async ValueTask DisposeAsync() => await Db.LockReleaseAsync(Key, Token);
///     }
/// }
/// </code>
/// </example>
public interface IConversationLock
{
    /// <summary>
    /// Waits until this process holds the lock of the conversation, and returns what releases it.
    /// </summary>
    /// <param name="key">The conversation: the queue key of the update, the same in every instance, such as <c>chat:123</c>.</param>
    /// <param name="ct">Cancelled when the bot stops.</param>
    ValueTask<IAsyncDisposable> AcquireAsync(string key, CancellationToken ct);
}
