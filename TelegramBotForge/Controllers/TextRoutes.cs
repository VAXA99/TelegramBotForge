using System.Text.RegularExpressions;

namespace TelegramBotForge.Controllers;

/// <summary>
/// Makes the method an action for a reply-keyboard button, which arrives as a text message with the button's label,
/// for example <c>[ButtonText("🛒 Catalog")]</c>. Give several labels for the same button in other languages.
/// </summary>
/// <remarks>
/// The text must equal a label exactly (spaces at the ends are ignored). Like commands, these actions win over
/// <c>[State]</c> actions, so a menu button works in the middle of a form.
/// </remarks>
/// <example>
/// <code>
/// [ButtonText("🛒 Catalog", "🛒 Каталог")]
/// public IBotResult Catalog() => Reply(catalog.Render());
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ButtonTextAttribute : RouteAttribute
{
    private readonly HashSet<string> _labels;

    /// <summary>
    /// Initializes a new instance of the <see cref="ButtonTextAttribute"/> class.
    /// </summary>
    /// <param name="labels">The labels of the button, at least one.</param>
    public ButtonTextAttribute(params string[] labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Length == 0 || labels.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("[ButtonText] needs at least one label, and labels cannot be empty.", nameof(labels));

        Labels = labels.Select(label => label.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        _labels = new HashSet<string>(Labels, StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the labels of the button.
    /// </summary>
    public IReadOnlyList<string> Labels { get; }

    /// <inheritdoc/>
    public override int Priority => 100;

    /// <inheritdoc/>
    public override string Key => "button text " + string.Join(" | ", Labels.Select(label => $"\"{label}\""));

    /// <inheritdoc/>
    public override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.Text is { } text && _labels.Contains(text.Trim());
}

/// <summary>
/// Makes the method an action for text that matches a regular expression. Named groups are bound to the parameters
/// with the same names and converted to their types, like <c>{placeholders}</c> of <c>[Callback]</c>.
/// </summary>
/// <remarks>
/// The pattern is searched in the text, so anchor it with <c>^...$</c> to match the whole message. A group that does not
/// take part in the match gives its parameter the default value. Every named group needs a parameter.
/// These actions are tried after commands and buttons and before <c>[Text]</c>; with <c>[State]</c> they apply only in that state.
/// </remarks>
/// <example>
/// <code>
/// [Regex(@"^(?&lt;amount&gt;\d+(\.\d+)?)\s*(?&lt;currency&gt;usd|eur)$", IgnoreCase = true)]
/// public IBotResult Convert(decimal amount, string currency) => Reply($"{amount} {currency.ToUpperInvariant()}");
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class RegexAttribute : RouteAttribute
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private readonly Lazy<Regex> _regex;
    private readonly Lazy<string[]> _groups;

    /// <summary>
    /// Initializes a new instance of the <see cref="RegexAttribute"/> class.
    /// </summary>
    /// <param name="pattern">The regular expression.</param>
    public RegexAttribute(string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        Pattern = pattern;
        _regex = new Lazy<Regex>(() => new Regex(pattern,
            RegexOptions.CultureInvariant | (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), MatchTimeout));
        _groups = new Lazy<string[]>(() => _regex.Value.GetGroupNames().Where(name => !int.TryParse(name, out _)).ToArray());

        // An invalid pattern stops the bot at startup with the action's name.
        _ = new Regex(pattern, RegexOptions.None, MatchTimeout);
    }

    /// <summary>
    /// Gets the regular expression.
    /// </summary>
    public string Pattern { get; }

    /// <summary>
    /// Gets or sets a value indicating whether letter case is ignored.
    /// </summary>
    public bool IgnoreCase { get; set; }

    /// <inheritdoc/>
    public override int Priority => 30;

    /// <inheritdoc/>
    public override string Key => "regex " + Pattern + (IgnoreCase ? " (ignore case)" : string.Empty);

    /// <inheritdoc/>
    public override IReadOnlyList<string> ParameterNames => _groups.Value;

    /// <inheritdoc/>
    public override bool TryMatch(BotContext context, IDictionary<string, string> values)
    {
        if (context.Text is not { } text)
            return false;

        Match match;
        try
        {
            match = _regex.Value.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        if (!match.Success)
            return false;

        foreach (var name in _groups.Value)
        {
            if (match.Groups[name] is { Success: true } group)
                values[name] = group.Value;
        }

        return true;
    }
}
