namespace TelegramBotForge.Configurations;

/// <summary>
/// Keeps outgoing messages under Telegram's limits, in the <c>TelegramBotConfiguration:FloodControl</c> section.
/// </summary>
/// <remarks>
/// Telegram answers <c>429 Too Many Requests</c> when a bot sends more than about 30 messages a second overall,
/// more than about one message a second to one chat, or more than 20 messages a minute to one group.
/// With flood control on, messages wait for their turn before they are sent, so the bot rarely gets a 429 at all.
/// When one still comes, the request is repeated after the time Telegram asks for.
/// Only sending, copying and forwarding messages waits; edits, button answers and other calls go out at once.
/// The limits apply to every message sent through the <c>ITelegramBotClient</c> from DI, broadcasts included.
/// </remarks>
public sealed class TelegramBotFloodControlOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether outgoing messages wait for their turn. On by default.
    /// When off, a 429 is still retried (see <see cref="RetryCount"/>).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how many messages the bot sends per second overall. Telegram's limit is about 30.
    /// </summary>
    public int GlobalPerSecond { get; set; } = 30;

    /// <summary>
    /// Gets or sets how many messages a private chat gets per second on average.
    /// </summary>
    public int PrivateChatPerSecond { get; set; } = 1;

    /// <summary>
    /// Gets or sets how many messages a private chat can get at once before <see cref="PrivateChatPerSecond"/> applies,
    /// so an answer of two or three messages is not slowed down.
    /// </summary>
    public int PrivateChatBurst { get; set; } = 3;

    /// <summary>
    /// Gets or sets how many messages a group, supergroup or channel gets per minute. Telegram's limit is 20.
    /// </summary>
    public int GroupPerMinute { get; set; } = 20;

    /// <summary>
    /// Gets or sets how many times a request is repeated after a 429. <c>0</c> turns retries off.
    /// </summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets the longest wait, in seconds, that a 429 is retried after. When Telegram asks to wait longer,
    /// the request fails with the 429 instead of blocking the conversation.
    /// </summary>
    public int MaxRetryAfterSeconds { get; set; } = 60;
}
