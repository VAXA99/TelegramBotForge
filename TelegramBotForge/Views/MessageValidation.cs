using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TelegramBotForge.Views;

internal static class MessageValidation
{
    private static readonly Regex Tags = new("<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9-]*)(?<attributes>(?:[^<>\"']|\"[^\"]*\"|'[^']*')*)>", RegexOptions.Compiled);
    private static readonly Regex Attributes = new("(?<name>[a-zA-Z][a-zA-Z0-9-]*)(?:\\s*=\\s*(?<value>\"[^\"]*\"|'[^']*'))?", RegexOptions.Compiled);
    private static readonly Regex NamedEntities = new("&[a-zA-Z]+;", RegexOptions.Compiled);
    private static readonly HashSet<string> StandardTags = ["b", "strong", "i", "em", "u", "ins", "s", "strike", "del", "a", "code", "pre", "tg-emoji", "tg-spoiler", "span", "blockquote"];
    private static readonly HashSet<string> RichTags = new(StandardTags)
    {
        "h1", "h2", "h3", "h4", "h5", "h6", "p", "br", "hr", "table", "tr", "td", "th", "caption",
        "details", "summary", "tg-button", "tg-button-row", "tg-slideshow", "tg-collage", "img", "video", "audio",
        "ul", "ol", "li", "input", "figure", "figcaption", "cite", "aside", "footer", "tg-reference", "tg-map",
        "mark", "sub", "sup", "tg-time", "tg-math", "tg-math-block", "tg-document"
    };
    private static readonly HashSet<string> VoidTags = ["br", "hr", "img", "input", "tg-map"];
    private static readonly HashSet<string> BlockTags = ["h1", "h2", "h3", "h4", "h5", "h6", "p", "pre", "hr", "table", "tr", "details", "tg-button-row", "tg-slideshow", "tg-collage", "img", "video", "audio", "ul", "ol", "li", "blockquote", "aside", "footer", "tg-map", "tg-math-block", "tg-document"];
    private static readonly HashSet<string> RichEntities = ["&lt;", "&gt;", "&amp;", "&quot;", "&apos;", "&nbsp;", "&hellip;", "&mdash;", "&ndash;", "&lsquo;", "&rsquo;", "&ldquo;", "&rdquo;"];

    internal static XElement Validate(string html, bool rich, string origin)
    {
        var normalized = Tags.Replace(html, tag =>
        {
            var name = tag.Groups["name"].Value;
            if (!(rich ? RichTags : StandardTags).Contains(name))
                throw Problem($"tag <{name}> is unsupported in {(rich ? "rich" : "standard")} mode.");
            if (tag.Groups["close"].Success)
                return tag.Value;
            var attributes = tag.Groups["attributes"].Value.TrimEnd();
            var selfClosing = attributes.EndsWith("/", StringComparison.Ordinal);
            if (selfClosing)
                attributes = attributes[..^1];
            var normalizedAttributes = new StringBuilder();
            var consumed = 0;
            foreach (Match attribute in Attributes.Matches(attributes))
            {
                if (!string.IsNullOrWhiteSpace(attributes[consumed..attribute.Index]))
                    throw Problem($"malformed attributes on <{name}>. Quote attribute values.");
                normalizedAttributes.Append(' ').Append(attribute.Groups["name"].Value).Append('=')
                    .Append(attribute.Groups["value"].Success ? attribute.Groups["value"].Value : "\"\"");
                consumed = attribute.Index + attribute.Length;
            }
            if (!string.IsNullOrWhiteSpace(attributes[consumed..]))
                throw Problem($"malformed attributes on <{name}>. Quote attribute values.");
            return $"<{name}{normalizedAttributes}{(selfClosing || VoidTags.Contains(name) ? "/" : "")}>";
        });
        normalized = NamedEntities.Replace(normalized, entity =>
        {
            if (!(rich ? RichEntities.Contains(entity.Value) : entity.Value is "&lt;" or "&gt;" or "&amp;" or "&quot;"))
                throw Problem($"unsupported named HTML entity {entity.Value}. Use a numeric entity.");
            return string.Concat(WebUtility.HtmlDecode(entity.Value).EnumerateRunes().Select(character => $"&#{character.Value};"));
        });
        XElement root;
        try { root = XElement.Parse("<root>" + normalized + "</root>", LoadOptions.PreserveWhitespace); }
        catch (XmlException exception) { throw Problem($"invalid HTML: {exception.Message}"); }
        var elements = root.Descendants().ToArray();
        var limit = rich ? 32768 : 4096;
        var length = rich ? root.Value.EnumerateRunes().Count() : root.Value.Length;
        if (length > limit)
            throw Problem($"{(rich ? "rich" : "standard")} text is {length} characters; the limit is {limit}.");
        if (rich && elements.Count(element => BlockTags.Contains(element.Name.LocalName)) > 500)
            throw Problem("rich content exceeds the 500 block limit.");
        if (rich && elements.Any(element => element.Ancestors().Count() > 16))
            throw Problem("rich content exceeds the 16 level nesting limit.");
        if (rich && elements.Count(element => element.Name.LocalName is "img" or "video" or "audio" or "tg-document" &&
                !((string?)element.Attribute("src") ?? "").StartsWith("tg://emoji?id=", StringComparison.Ordinal)) > 50)
            throw Problem("rich content exceeds the 50 media attachment limit.");
        foreach (var element in elements)
        {
            var name = element.Name.LocalName;
            if (!rich && name == "span" && (string?)element.Attribute("class") != "tg-spoiler")
                throw Problem("standard <span> requires class=\"tg-spoiler\".");
            if (name == "table" && !element.Elements("tr").Any())
                throw Problem("a table requires at least one row.");
            if (name == "tr" && !element.Elements().Any(cell => cell.Name.LocalName is "td" or "th"))
                throw Problem("a table row requires at least one cell.");
            var hasInlineAncestor = element.Ancestors().Any(ancestor => ancestor.Name.LocalName is
                "b" or "strong" or "i" or "em" or "u" or "ins" or "s" or "strike" or "del" or "a" or "tg-emoji" or "tg-spoiler" or "span");
            if (!rich && name == "code" && hasInlineAncestor)
                throw Problem("standard code cannot be nested inside other inline formatting.");
            if (name == "pre" && (hasInlineAncestor || element.Ancestors().Any(ancestor => ancestor.Name.LocalName is
                    "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "td" or "th" or "summary")))
                throw Problem("preformatted text requires a separate paragraph.");
            if (name == "a" && element.Ancestors("a").Any())
                throw Problem("links cannot contain other links.");
            if (rich && name == "tr" && element.Elements().Sum(cell => (int?)cell.Attribute("colspan") ?? 1) > 20)
                throw Problem("rich tables support at most 20 columns.");
            if (name == "tg-button")
            {
                if (element.Descendants().Any(child => child.Name.LocalName is not ("tg-emoji" or "tg-time")))
                    throw Problem("rich button labels allow only plain text, custom emoji and dates.");
                if ((string?)element.Attribute("type") == "callback_data")
                {
                    var data = (string?)element.Attribute("data") ?? "";
                    if (Encoding.UTF8.GetByteCount(data) is < 1 or > 64)
                        throw Problem("raw callback data must contain 1-64 UTF-8 bytes.");
                }
            }
            if (name == "tg-button-row" && element.Elements("tg-button").Count() is < 1 or > 8)
                throw Problem("a rich button row requires 1-8 buttons.");
            if (name is "td" or "th" or "summary" && element.Descendants().Any(child => BlockTags.Contains(child.Name.LocalName)))
                throw Problem($"<{name}> supports only inline content.");
            if (name is "img" or "video" or "audio" && element.Ancestors().Any(ancestor => ancestor.Name.LocalName is "p" or "a" or "tg-button"))
                throw Problem("rich media must be a separate block.");
        }
        if (elements.Count(element => element.Name.LocalName == "tg-button") > 100)
            throw Problem("the message has more than 100 buttons.");
        return root;
        InvalidOperationException Problem(string problem) => new($"View {origin}: {problem}");
    }
}
