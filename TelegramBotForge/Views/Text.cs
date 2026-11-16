using System.Net;

namespace TelegramBotForge.Views;

/// <summary>Immutable inline content. Strings are always text, never markup.</summary>
public sealed class Text
{
    private readonly string _tag;
    private readonly string _value;
    private readonly Text[] _children;

    private Text(string tag, string value, params Text[] children)
    {
        _tag = tag;
        _value = value;
        _children = children.ToArray();
    }

    public static implicit operator Text(string? value) => Plain(value);
    public static Text Plain(string? value) => new("", value ?? "");
    public static Text Bold(Text content) => new("b", "", content);
    public static Text Italic(Text content) => new("i", "", content);
    public static Text Underline(Text content) => new("u", "", content);
    public static Text Strike(Text content) => new("s", "", content);
    public static Text Code(string content) => new("code", "", Plain(content));
    public static Text Preformatted(string content) => new("pre", "", Plain(content));
    public static Text Link(Text label, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            throw new ArgumentException("A link needs an absolute URL.", nameof(url));
        return new("a", url, label);
    }
    public static Text Emoji(string customEmojiId, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customEmojiId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        return new("tg-emoji", customEmojiId, Plain(fallback));
    }
    public static Text Concat(params Text[] content) => new("span", "", content);
    public static Text Join(Text separator, IEnumerable<Text> content) => Concat(content
        .SelectMany((part, index) => index == 0 ? new[] { part } : new[] { separator, part }).ToArray());

    internal string PlainText => _tag.Length == 0 ? _value : string.Concat(_children.Select(child => child.PlainText));
    internal bool IsPreformatted => _tag == "pre";
    internal bool ButtonCompatible => _tag is "" or "span" or "tg-emoji"
        && _children.All(child => child.ButtonCompatible);
    internal string Html => _tag switch
    {
        "" => WebUtility.HtmlEncode(_value),
        "span" => string.Concat(_children.Select(child => child.Html)),
        "a" => $"<a href=\"{WebUtility.HtmlEncode(_value)}\">{Body}</a>",
        "tg-emoji" => $"<tg-emoji emoji-id=\"{WebUtility.HtmlEncode(_value)}\">{Body}</tg-emoji>",
        _ => $"<{_tag}>{Body}</{_tag}>"
    };
    private string Body => string.Concat(_children.Select(child => child.Html));
}
