using TelegramBotForge.Middleware;

namespace TelegramBotForge.Configurations;

/// <summary>
/// Settings of the built-in <see cref="RateLimitMiddleware"/>, bound from the <c>RateLimit</c> subsection.
/// </summary>
/// <example>
/// <code>
/// "RateLimit": { "Enabled": true, "MaxUpdates": 30, "PerSeconds": 60 }
/// </code>
/// </example>
public sealed class TelegramBotRateLimitOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether users are limited. The default is <c>false</c>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets how many updates (messages, button presses) one user may send in <see cref="PerSeconds"/>. The default is 30.
    /// </summary>
    public int MaxUpdates { get; set; } = 30;

    /// <summary>
    /// Gets or sets the length of the window, in seconds. The default is 60.
    /// </summary>
    public int PerSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the text sent once when a user hits the limit. Further updates in the same window are ignored silently.
    /// Set it to an empty string to send nothing.
    /// </summary>
    public string Message { get; set; } = "You're sending messages too fast. Please wait a moment.";

    /// <summary>
    /// Gets or sets user ids that are never limited, for example the bot's admins.
    /// </summary>
    public long[] ExemptUserIds { get; set; } = [];
}
