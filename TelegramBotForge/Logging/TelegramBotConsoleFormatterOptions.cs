using Microsoft.Extensions.Logging.Console;

namespace TelegramBotForge.Logging;

/// <summary>
/// Options of <see cref="TelegramBotConsoleFormatter"/>. They can also be set in configuration under
/// <c>Logging:Console:FormatterOptions</c>.
/// </summary>
public sealed class TelegramBotConsoleFormatterOptions : ConsoleFormatterOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramBotConsoleFormatterOptions"/> class
    /// with scopes turned on and a short time stamp.
    /// </summary>
    public TelegramBotConsoleFormatterOptions()
    {
        IncludeScopes = true;
        TimestampFormat = "HH:mm:ss.fff";
    }

    /// <summary>
    /// Gets or sets a value indicating whether the output is colored with ANSI escape codes.
    /// The default is <c>true</c>.
    /// </summary>
    public bool UseColors { get; set; } = true;
}
