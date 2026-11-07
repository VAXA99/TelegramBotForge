using System.Collections;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace TelegramBotForge.Logging;

/// <summary>
/// A compact, colored console format for bot logs:
/// <c>12:04:31.512 INF Worker     │ text "/start" from @user (42) → StartController.Start → SendText (12 ms)</c>.
/// Values in the message are highlighted, TelegramBotForge categories are colored differently from your own,
/// and log scopes (such as the chat and user of the update being processed) are appended in a dim color.
/// </summary>
public sealed class TelegramBotConsoleFormatter : ConsoleFormatter, IDisposable
{
    /// <summary>
    /// The name under which the formatter is registered, for use in <c>Logging:Console:FormatterName</c>.
    /// </summary>
    public const string FormatterName = "telegrambotforge";

    private const int CategoryWidth = 12;
    private const string Reset = "\u001b[0m";
    private const string Bold = "\u001b[1m";
    private const string Dim = "\u001b[2m";
    private const string Red = "\u001b[31m";
    private const string Green = "\u001b[32m";
    private const string Yellow = "\u001b[33m";
    private const string Blue = "\u001b[34m";
    private const string Magenta = "\u001b[35m";
    private const string Cyan = "\u001b[36m";
    private const string Gray = "\u001b[90m";
    private const string ErrorBadge = "\u001b[1;97;41m";
    private const string CriticalBadge = "\u001b[1;97;45m";

    private readonly IDisposable? _optionsReload;
    private TelegramBotConsoleFormatterOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelegramBotConsoleFormatter"/> class.
    /// </summary>
    /// <param name="options">The formatter options.</param>
    public TelegramBotConsoleFormatter(IOptionsMonitor<TelegramBotConsoleFormatterOptions> options)
        : base(FormatterName)
    {
        _options = options.CurrentValue;
        _optionsReload = options.OnChange(updated => _options = updated);
    }

    /// <inheritdoc/>
    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
            return;

        var options = _options;
        var line = new Line(options.UseColors && Environment.GetEnvironmentVariable("NO_COLOR") is null);

        var now = options.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
        line.Append(Gray, now.ToString(options.TimestampFormat ?? "HH:mm:ss.fff", CultureInfo.InvariantCulture));
        line.Plain(" ");
        AppendLevel(line, logEntry.LogLevel);
        line.Plain(" ");

        var (category, isLibrary) = ShortenCategory(logEntry.Category);
        line.Append(isLibrary ? Magenta : Blue, category.PadRight(CategoryWidth));
        line.Append(Gray, " │ ");

        var messageColor = logEntry.LogLevel switch
        {
            LogLevel.Warning => Yellow,
            >= LogLevel.Error => Red,
            LogLevel.Trace or LogLevel.Debug => Gray,
            _ => null
        };

        if (!line.Colored ||
            logEntry.State is not IReadOnlyList<KeyValuePair<string, object?>> properties ||
            !TryAppendTemplate(line, properties, messageColor))
        {
            line.Append(messageColor, message ?? string.Empty);
        }

        if (options.IncludeScopes && scopeProvider is not null)
            AppendScopes(line, scopeProvider);

        line.Plain(Environment.NewLine);

        if (logEntry.Exception is { } exception)
            AppendException(line, exception);

        textWriter.Write(line.ToString());
    }

    /// <inheritdoc/>
    public void Dispose() => _optionsReload?.Dispose();

    private static void AppendLevel(Line line, LogLevel level)
    {
        switch (level)
        {
            case LogLevel.Trace: line.Append(Gray, "TRC"); break;
            case LogLevel.Debug: line.Append(Cyan, "DBG"); break;
            case LogLevel.Information: line.Append(Bold + Green, "INF"); break;
            case LogLevel.Warning: line.Append(Bold + Yellow, "WRN"); break;
            case LogLevel.Error: line.Append(ErrorBadge, "ERR"); break;
            case LogLevel.Critical: line.Append(CriticalBadge, "CRT"); break;
            default: line.Plain("   "); break;
        }
    }

    /// <summary>
    /// Turns <c>TelegramBotForge.Routing.BotRouter</c> into <c>BotRouter</c> and
    /// <c>Microsoft.Hosting.Lifetime</c> into <c>Lifetime</c>, shortened to the category column width.
    /// </summary>
    internal static (string Name, bool IsLibrary) ShortenCategory(string category)
    {
        var isLibrary = category.StartsWith("TelegramBotForge", StringComparison.Ordinal);

        var name = category;
        var generic = name.IndexOf('`');
        if (generic >= 0)
            name = name[..generic];
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
            name = name[(dot + 1)..];
        if (isLibrary && name.StartsWith("TelegramBot", StringComparison.Ordinal) && name.Length > 11)
            name = name[11..];

        if (name.Length > CategoryWidth)
            name = name[..(CategoryWidth - 1)] + "…";

        return (name, isLibrary);
    }

    /// <summary>
    /// Renders a structured message template with every value highlighted.
    /// Returns <c>false</c> when the state has no template, so the caller falls back to the plain message.
    /// </summary>
    internal static bool TryAppendTemplate(
        Line line,
        IReadOnlyList<KeyValuePair<string, object?>> properties,
        string? textColor)
    {
        string? template = null;
        foreach (var property in properties)
        {
            if (property.Key == "{OriginalFormat}")
            {
                template = property.Value as string;
                break;
            }
        }

        if (template is null)
            return false;

        var text = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];
            if (c == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                text.Append('{');
                i += 2;
                continue;
            }

            if (c == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                text.Append('}');
                i += 2;
                continue;
            }

            var end = c == '{' ? template.IndexOf('}', i + 1) : -1;
            if (end < 0)
            {
                text.Append(c);
                i++;
                continue;
            }

            line.Append(textColor, text.ToString());
            text.Clear();

            var hole = template.AsSpan(i + 1, end - i - 1);
            var formatStart = hole.IndexOfAny(',', ':');
            var format = formatStart >= 0 && hole[formatStart] == ':' ? hole[(formatStart + 1)..].ToString() : null;
            var name = (formatStart >= 0 ? hole[..formatStart] : hole).TrimStart("@$").ToString();

            line.Append(Bold + Cyan, FormatValue(Find(properties, name), format));
            i = end + 1;
        }

        line.Append(textColor, text.ToString());
        return true;
    }

    private static object? Find(IReadOnlyList<KeyValuePair<string, object?>> properties, string name)
    {
        foreach (var property in properties)
        {
            if (property.Key == name)
                return property.Value;
        }

        return null;
    }

    internal static string FormatValue(object? value, string? format = null) => value switch
    {
        null => "null",
        string text => text,
        IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
        IEnumerable items => string.Join(", ", items.Cast<object?>().Select(item => FormatValue(item))),
        _ => value.ToString() ?? string.Empty
    };

    private static void AppendScopes(Line line, IExternalScopeProvider scopeProvider)
    {
        scopeProvider.ForEachScope(static (scope, target) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    if (key == "{OriginalFormat}")
                        continue;
                    target.Append(Gray, $"  {key}={FormatValue(value)}");
                }
            }
            else if (scope is not null)
            {
                target.Append(Gray, $"  {scope}");
            }
        }, line);
    }

    private static void AppendException(Line line, Exception exception)
    {
        foreach (var exceptionLine in exception.ToString().Split('\n'))
        {
            var trimmed = exceptionLine.TrimEnd('\r');
            if (trimmed.Length == 0)
                continue;

            var isFrame = trimmed.TrimStart().StartsWith("at ", StringComparison.Ordinal);
            line.Append(isFrame ? Gray : Red, "    " + trimmed);
            line.Plain(Environment.NewLine);
        }
    }

    /// <summary>
    /// Builds one log entry and adds ANSI color codes only when colors are on.
    /// </summary>
    internal sealed class Line
    {
        private readonly StringBuilder _builder = new();

        public Line(bool colored) => Colored = colored;

        public bool Colored { get; }

        public void Plain(string text) => _builder.Append(text);

        public void Append(string? color, string text)
        {
            if (text.Length == 0)
                return;

            if (!Colored || color is null)
            {
                _builder.Append(text);
                return;
            }

            _builder.Append(color).Append(text).Append(Reset);
        }

        public override string ToString() => _builder.ToString();
    }
}
