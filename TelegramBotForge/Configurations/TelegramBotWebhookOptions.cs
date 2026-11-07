namespace TelegramBotForge.Configurations;

/// <summary>
/// Webhook settings, bound from the <c>Webhook</c> subsection. Used when the bot calls <c>UseWebhook()</c>
/// from <c>TelegramBotForge.AspNetCore</c>; the address changes between environments, so it stays in configuration.
/// </summary>
/// <example>
/// <code>
/// "Webhook": { "Url": "https://bot.example.com/telegram/webhook", "SecretToken": "a-long-random-string" }
/// </code>
/// </example>
public sealed class TelegramBotWebhookOptions
{
    /// <summary>
    /// Gets or sets the public HTTPS URL of the webhook endpoint, including its path.
    /// The bot registers it with Telegram at startup.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets a secret that Telegram sends in the <c>X-Telegram-Bot-Api-Secret-Token</c> header of every request,
    /// so the endpoint can reject requests that do not come from Telegram. 1–256 characters: letters, digits, <c>_</c> and <c>-</c>.
    /// Strongly recommended.
    /// </summary>
    public string? SecretToken { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether updates that arrived while the bot was offline are dropped when the webhook is registered.
    /// The default is <c>false</c>.
    /// </summary>
    public bool DropPendingUpdates { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the webhook is removed when the application stops.
    /// Leave it <c>false</c> when several instances serve the same bot or during rolling deployments. The default is <c>false</c>.
    /// </summary>
    public bool DeleteOnShutdown { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of simultaneous connections Telegram opens to the endpoint (1–100).
    /// When it is not set, Telegram uses 40.
    /// </summary>
    public int? MaxConnections { get; set; }
}
