using Microsoft.Extensions.Logging;

namespace TelegramBotForge.Configurations;

/// <summary>
/// Logging settings of TelegramBotForge, bound from the <c>Logging</c> subsection of the bot configuration section.
/// </summary>
/// <example>
/// <code>
/// "TelegramBotConfiguration": {
///   "Token": "...",
///   "Logging": {
///     "Enabled": true,
///     "MinimumLevel": "Information",
///     "PrettyConsole": true,
///     "UseColors": true,
///     "LogUpdates": true,
///     "IncludeMessageText": true,
///     "SlowUpdateWarningMs": 3000
///   }
/// }
/// </code>
/// </example>
public sealed class TelegramBotLoggingOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether TelegramBotForge writes logs.
    /// When <c>false</c>, every log category that starts with <c>TelegramBotForge</c> is turned off. Your own logs are not affected.
    /// The default is <c>true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the lowest level that TelegramBotForge logs. Use <see cref="LogLevel.Debug"/> to also see queues,
    /// webhook registration and other internal details. The default is <see cref="LogLevel.Information"/>.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Gets or sets a value indicating whether the console uses the compact TelegramBotForge format:
    /// time, a short level badge, a short category, the message with highlighted values, and the chat and user of the update.
    /// It applies to every console log line of the application, not only to TelegramBotForge.
    /// Set it to <c>false</c> to keep your own console logging setup. The default is <c>true</c>.
    /// </summary>
    public bool PrettyConsole { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="PrettyConsole"/> output is colored.
    /// Colors are also turned off when the <c>NO_COLOR</c> environment variable is set. The default is <c>true</c>.
    /// </summary>
    public bool UseColors { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether every processed update is logged at <see cref="LogLevel.Information"/>
    /// with its type, sender, action, result and processing time.
    /// When <c>false</c>, these lines are logged at <see cref="LogLevel.Debug"/>. The default is <c>true</c>.
    /// </summary>
    public bool LogUpdates { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether update log lines show the beginning of the message text, captions and button data.
    /// Set it to <c>false</c> in production if message content must not appear in logs; only the length is logged then.
    /// The default is <c>true</c>.
    /// </summary>
    public bool IncludeMessageText { get; set; } = true;

    /// <summary>
    /// Gets or sets the processing time, in milliseconds, after which an update is logged as a warning.
    /// Set it to 0 to turn the warning off. The default is 3000.
    /// </summary>
    public int SlowUpdateWarningMs { get; set; } = 3000;
}
