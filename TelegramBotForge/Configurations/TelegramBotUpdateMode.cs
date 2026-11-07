namespace TelegramBotForge.Configurations;

/// <summary>
/// How the bot receives updates from Telegram.
/// </summary>
internal enum TelegramBotUpdateMode
{
    /// <summary>
    /// The bot asks Telegram for new updates in a loop. Works anywhere, including on a developer machine. The default.
    /// </summary>
    Polling,

    /// <summary>
    /// Telegram sends every update to an HTTPS endpoint of the application. Needs a public URL;
    /// turned on by <c>UseWebhook()</c> from the <c>TelegramBotForge.AspNetCore</c> package.
    /// </summary>
    Webhook
}
