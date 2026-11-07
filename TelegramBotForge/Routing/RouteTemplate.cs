using System.Text;
using System.Text.RegularExpressions;

namespace TelegramBotForge.Routing;

/// <summary>
/// A template of button data such as <c>buy:{productId:int}</c>, parsed once at startup.
/// </summary>
internal sealed class RouteTemplate
{
    private const int MaxCallbackDataBytes = 64;

    private static readonly Regex Placeholder = new(
        @"\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::(?<constraint>[A-Za-z]+))?\}",
        RegexOptions.Compiled);

    private readonly Regex _regex;

    public RouteTemplate(string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);
        Template = template;

        var names = new List<string>();
        var pattern = new StringBuilder("^");
        var literal = new StringBuilder();
        var last = 0;

        foreach (Match match in Placeholder.Matches(template))
        {
            var text = template[last..match.Index];
            literal.Append(text);
            pattern.Append(Regex.Escape(text));

            var name = match.Groups["name"].Value;
            if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"The callback template \"{template}\" uses {{{name}}} twice.");

            var constraint = match.Groups["constraint"].Success ? match.Groups["constraint"].Value : null;
            pattern.Append("(?<").Append(name).Append('>').Append(PatternFor(constraint, template)).Append(')');
            names.Add(name);
            last = match.Index + match.Length;
        }

        var rest = template[last..];
        literal.Append(rest);
        pattern.Append(Regex.Escape(rest)).Append('$');

        var fixedText = literal.ToString();
        if (fixedText.Contains('{') || fixedText.Contains('}'))
            throw new ArgumentException($"The callback template \"{template}\" has a malformed {{placeholder}}.");
        if (Encoding.UTF8.GetByteCount(fixedText) > MaxCallbackDataBytes)
            throw new ArgumentException($"The callback template \"{template}\" is longer than Telegram's {MaxCallbackDataBytes}-byte limit.");

        _regex = new Regex(pattern.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant);
        ParameterNames = names;
    }

    public string Template { get; }

    public IReadOnlyList<string> ParameterNames { get; }

    public bool TryMatch(string input, IDictionary<string, string> values)
    {
        var match = _regex.Match(input);
        if (!match.Success)
            return false;

        foreach (var name in ParameterNames)
            values[name] = match.Groups[name].Value;
        return true;
    }

    private static string PatternFor(string? constraint, string template) => constraint?.ToLowerInvariant() switch
    {
        null => "[^:]+",
        "int" or "long" => "-?[0-9]+",
        _ => throw new ArgumentException(
            $"Unknown constraint :{constraint} in the callback template \"{template}\". Supported: int, long.")
    };
}
