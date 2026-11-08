namespace TelegramBotForge.Hosting;

/// <summary>
/// Who the bot is and how its connection to Telegram is doing, filled in by the receiver. Read by the health check.
/// </summary>
internal sealed class BotIdentity
{
    public volatile string? Username;

    /// <summary>
    /// Telegram answered 401: the token is wrong or was revoked.
    /// </summary>
    public volatile bool TokenRejected;

    /// <summary>
    /// The webhook could not be registered, so no updates arrive.
    /// </summary>
    public volatile bool WebhookFailed;

    private long _lastConflictTicks;

    /// <summary>
    /// When Telegram last answered 409: another process polls with the same token, or a webhook is set.
    /// </summary>
    public DateTimeOffset? LastConflict
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastConflictTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        set => Interlocked.Exchange(ref _lastConflictTicks, value?.UtcTicks ?? 0);
    }
}
