using System.Text.RegularExpressions;
using TelegramBotForge.Media;
using TelegramBotForge.Routing;
using TelegramBotForge.State;

namespace TelegramBotForge.Controllers;

/// <summary>
/// The base of the attributes that make a controller method an action.
/// </summary>
public abstract class RouteAttribute : Attribute
{
    private protected RouteAttribute()
    {
    }

    /// <summary>
    /// Gets the priority: routes with a higher priority are tried first.
    /// </summary>
    internal abstract int Priority { get; }

    /// <summary>
    /// Gets a key that is equal for routes that match the same updates, to find duplicates at startup.
    /// </summary>
    internal abstract string Key { get; }

    /// <summary>
    /// Gets the names of the {placeholders} whose values are bound to action parameters.
    /// </summary>
    internal virtual IReadOnlyList<string> ParameterNames => Array.Empty<string>();

    /// <summary>
    /// Checks whether the update targets this route and collects its {placeholder} values.
    /// </summary>
    internal abstract bool TryMatch(BotContext context, IDictionary<string, string> values);
}

/// <summary>
/// Makes the method an action for a command, for example <c>[Command("start")]</c> for <c>/start</c>.
/// Also matches <c>/start@MyBot</c> and <c>/start with more text</c>. Repeat it for aliases.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CommandAttribute : RouteAttribute
{
    private static readonly Regex ValidName = new("^[A-Za-z0-9_]{1,32}$", RegexOptions.Compiled);
    private static readonly char[] Separators = [' ', '\n', '@'];

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandAttribute"/> class.
    /// </summary>
    /// <param name="name">The command, with or without the leading <c>/</c>: 1–32 letters, digits or <c>_</c>.</param>
    public CommandAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name.TrimStart('/');
        if (!ValidName.IsMatch(Name))
            throw new ArgumentException($"\"{name}\" is not a valid command: use 1-32 letters, digits or _.", nameof(name));
    }

    /// <summary>
    /// Gets the command without the leading <c>/</c>.
    /// </summary>
    public string Name { get; }

    internal override int Priority => 100;

    internal override string Key => "command /" + Name.ToLowerInvariant();

    internal override bool TryMatch(BotContext context, IDictionary<string, string> values)
    {
        var text = context.Text;
        if (string.IsNullOrEmpty(text) || text[0] != '/')
            return false;

        var end = text.IndexOfAny(Separators);
        var command = end < 0 ? text[1..] : text[1..end];
        return string.Equals(command, Name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Makes the method an action for a button press whose data matches a template, for example
/// <c>[Callback("buy:{productId:int}")]</c>. Placeholder values are bound to the parameters with the same name.
/// </summary>
/// <remarks>
/// <c>{name}</c> matches anything except <c>:</c>; <c>{name:int}</c> and <c>{name:long}</c> match whole numbers.
/// Telegram allows at most 64 bytes of button data, so keep templates short.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CallbackAttribute : RouteAttribute
{
    private readonly RouteTemplate _template;

    /// <summary>
    /// Initializes a new instance of the <see cref="CallbackAttribute"/> class.
    /// </summary>
    /// <param name="template">The template of the button data, for example <c>buy:{productId:int}</c>.</param>
    public CallbackAttribute(string template)
    {
        _template = new RouteTemplate(template);
    }

    /// <summary>
    /// Gets the template of the button data.
    /// </summary>
    public string Template => _template.Template;

    internal override int Priority => 100;

    internal override string Key => "callback " + _template.Template;

    internal override IReadOnlyList<string> ParameterNames => _template.ParameterNames;

    internal override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.CallbackData is { } data && _template.TryMatch(data, values);
}

/// <summary>
/// Makes the method an action for any text message that is not a command.
/// Usually combined with <see cref="StateAttribute"/> to read the answer to a question.
/// The text is bound to a <c>string text</c> parameter.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TextAttribute : RouteAttribute
{
    internal override int Priority => 10;

    internal override string Key => "text";

    internal override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.Text is { } text && !text.StartsWith('/');
}

/// <summary>
/// Makes the method an action for a message with media, optionally only some types, for example
/// <c>[Media(MediaType.Photo, MediaType.Video)]</c>. The media is bound to a <c>BotMedia</c> parameter.
/// </summary>
/// <remarks>
/// An album arrives as one update per item, so the action runs once for each of them.
/// Actions for specific types are tried before <c>[Media]</c> without types.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class MediaAttribute : RouteAttribute
{
    private readonly HashSet<MediaType>? _types;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaAttribute"/> class.
    /// </summary>
    /// <param name="types">The media types the action accepts. None means all.</param>
    public MediaAttribute(params MediaType[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        Types = types.Distinct().Order().ToArray();
        _types = Types.Count == 0 ? null : new HashSet<MediaType>(Types);
    }

    /// <summary>
    /// Gets the media types the action accepts; empty means all.
    /// </summary>
    public IReadOnlyList<MediaType> Types { get; }

    internal override int Priority => _types is null ? 20 : 30;

    internal override string Key => _types is null ? "media" : "media " + string.Join(", ", Types);

    internal override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.Media is { } media && (_types is null || _types.Contains(media.Type));
}

/// <summary>
/// Makes the method an action for a location the user shares, bound to a <c>Location</c> parameter
/// (Telegram's own type). A shared venue has a location too and is available as a <c>Venue</c> parameter.
/// </summary>
/// <remarks>
/// For a live location, the action runs for the first message only; the later position updates are not routed.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LocationAttribute : RouteAttribute
{
    internal override int Priority => 20;

    internal override string Key => "location";

    internal override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.Location is not null;
}

/// <summary>
/// Limits an action to users in one of the given conversation states, for example <c>[State(Registration.Age)]</c>.
/// On its own, without a route attribute, the action receives any message (text, contact, photo...) in that state.
/// Commands still win over it, so <c>/cancel</c> works in every state.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StateAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StateAttribute"/> class.
    /// </summary>
    /// <param name="states">Enum values or names. The action applies in any of them.</param>
    public StateAttribute(params object[] states)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (states.Length == 0)
            throw new ArgumentException("[State] needs at least one state.", nameof(states));

        States = states.Select(StateNames.Of).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Gets the state names the action applies in.
    /// </summary>
    public IReadOnlyList<string> States { get; }
}
