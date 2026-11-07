namespace TelegramBotForge.Configurations;

/// <summary>
/// Configuration for the TelegramBotForge runtime, bound from the <c>TelegramBotConfiguration</c> section.
/// It holds what can change between environments without changing code: the token, the webhook address, the Telegram API client,
/// queue sizes, rate limiting and logging. How the bot behaves (callback answers, queue grouping, state lifetime, webhook mode)
/// is set in code on <see cref="TelegramBotBuilder"/>.
/// Invalid values stop the application at startup with a message that lists every problem (see <see cref="TelegramBotConfigurationValidator"/>).
/// </summary>
public sealed class TelegramBotConfiguration
{
    /// <summary>
    /// The name of the configuration section the settings are bound from.
    /// </summary>
    public const string SectionName = "TelegramBotConfiguration";

    /// <summary>
    /// Gets or sets the Telegram bot token used by the shared <see cref="Telegram.Bot.ITelegramBotClient"/>.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum number of updates processed at the same time across all queues.
    /// </summary>
    public int MaxGlobalConcurrency { get; set; } = 32;

    /// <summary>
    /// Gets or sets how many updates one queue (a chat, a user or a chat/user pair, see <see cref="TelegramBotBuilder.QueueBy"/>) can hold.
    /// </summary>
    public int PerChatQueueCapacity { get; set; } = 200;

    /// <summary>
    /// Gets or sets what happens when a queue is full: <c>true</c> drops its oldest update (with a warning in the log),
    /// <c>false</c> waits for free space.
    /// </summary>
    /// <remarks>
    /// Waiting pauses receiving new updates for all chats until the full queue has room again.
    /// In webhook mode it makes the webhook request wait.
    /// </remarks>
    public bool DropOnOverflow { get; set; }

    /// <summary>
    /// Gets or sets how long, in seconds, an idle queue is kept before it is closed and removed.
    /// A new update simply creates it again.
    /// </summary>
    public int IdleChatTtlSeconds { get; set; } = 600;

    /// <summary>
    /// Gets or sets the overall HTTP request timeout for the Telegram API, in seconds.
    /// </summary>
    public int TelegramHttpTimeoutSeconds { get; set; } = 100;

    /// <summary>
    /// Gets or sets the TCP connection timeout for the Telegram API, in seconds.
    /// </summary>
    public int TelegramConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Gets or sets how long a pooled HTTP connection can stay idle before it is closed, in seconds.
    /// </summary>
    public int TelegramPooledConnectionIdleTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the maximum lifetime of a pooled HTTP connection, in seconds.
    /// </summary>
    public int TelegramPooledConnectionLifetimeSeconds { get; set; } = 120;

    /// <summary>
    /// Gets or sets the base retry delay for polling network errors, in seconds.
    /// </summary>
    public int PollingNetworkErrorBaseDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum retry delay for repeated polling network errors, in seconds.
    /// </summary>
    public int PollingNetworkErrorMaxDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the number of consecutive polling network errors after which they are logged as errors.
    /// </summary>
    public int PollingNetworkErrorErrorThreshold { get; set; } = 10;

    /// <summary>
    /// Gets or sets the text sent to the user when an action fails.
    /// Set it to an empty string to send nothing.
    /// </summary>
    public string FallbackErrorMessage { get; set; } = "An error occurred";

    /// <summary>
    /// Gets or sets the text sent to the user when a text message matches no route.
    /// Set it to an empty string to send nothing.
    /// </summary>
    public string UnknownCommandMessage { get; set; } = "Unknown command";

    /// <summary>
    /// Gets or sets the update types to receive from Telegram, for example <c>["Message", "CallbackQuery", "MyChatMember"]</c>.
    /// When it is not set, Telegram sends all update types except <c>ChatMember</c>, <c>MessageReaction</c> and <c>MessageReactionCount</c>,
    /// which must be requested explicitly.
    /// </summary>
    public Telegram.Bot.Types.Enums.UpdateType[]? AllowedUpdates { get; set; }

    /// <summary>
    /// Gets or sets an optional Bot API server URL, for example a self-hosted <c>telegram-bot-api</c> server.
    /// When it is not set, the official <c>https://api.telegram.org</c> server is used.
    /// </summary>
    public string? BotApiBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the webhook settings, used when the bot calls <c>UseWebhook()</c> from <c>TelegramBotForge.AspNetCore</c>.
    /// </summary>
    public TelegramBotWebhookOptions Webhook { get; set; } = new();

    /// <summary>
    /// Gets or sets the settings of the built-in rate limit, which is off by default.
    /// </summary>
    public TelegramBotRateLimitOptions RateLimit { get; set; } = new();

    /// <summary>
    /// Gets or sets the settings of Telegram Stars payments, used when <c>AddStars</c> is called.
    /// </summary>
    public TelegramBotStarsOptions Stars { get; set; } = new();

    /// <summary>
    /// Gets or sets the logging settings: whether TelegramBotForge logs at all, the minimum level and the colored console format.
    /// </summary>
    public TelegramBotLoggingOptions Logging { get; set; } = new();
}
