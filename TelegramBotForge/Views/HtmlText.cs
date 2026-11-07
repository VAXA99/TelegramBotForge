using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace TelegramBotForge.Views;

/// <summary>
/// HTML that is inserted as it is, without escaping. Create it with <c>Raw(html)</c> inside a view.
/// Only use it for HTML you built yourself, never for text that came from a user.
/// </summary>
/// <param name="Html">The HTML.</param>
public readonly record struct RawHtml(string Html);

/// <summary>
/// One line of a view. Write it as an interpolated string: the fixed parts are HTML, and every <c>{value}</c>
/// is escaped automatically, so text from users can never break or inject formatting.
/// </summary>
/// <example>
/// <code>
/// message.Line($"&lt;b&gt;Name:&lt;/b&gt; {user.Name}")    // the name is escaped, the &lt;b&gt; stays bold
/// message.Line($"{Raw(prebuiltHtml)}")                  // inserted without escaping
/// message.Text(user.Bio)                                // a whole string as plain text
/// </code>
/// </example>
/// <remarks>
/// Only interpolated strings are accepted, so a string variable cannot slip in unescaped:
/// <c>Line(name)</c> does not compile; write <c>Line($"{name}")</c>, or <c>Text(name)</c> for a whole line of plain text.
/// Numbers and dates are formatted with the invariant culture.
/// </remarks>
[InterpolatedStringHandler]
public ref struct HtmlText
{
    private readonly StringBuilder _html;

    /// <summary>
    /// Used by the compiler for interpolated strings.
    /// </summary>
    public HtmlText(int literalLength, int formattedCount)
    {
        _html = new StringBuilder(literalLength + formattedCount * 8);
    }

    /// <summary>
    /// Adds a fixed part of the interpolated string: HTML, not escaped.
    /// </summary>
    public void AppendLiteral(string value) => _html.Append(value);

    /// <summary>
    /// Adds a value, escaped.
    /// </summary>
    public void AppendFormatted(string? value) => _html.Append(WebUtility.HtmlEncode(value));

    /// <summary>
    /// Adds raw HTML, not escaped.
    /// </summary>
    public void AppendFormatted(RawHtml value) => _html.Append(value.Html);

    /// <summary>
    /// Adds a value, escaped.
    /// </summary>
    public void AppendFormatted<T>(T value) => AppendFormatted(value, alignment: 0, format: null);

    /// <summary>
    /// Adds a formatted value, for example <c>{price:0.00}</c>, escaped.
    /// </summary>
    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value, alignment: 0, format);

    /// <summary>
    /// Adds a padded value, for example <c>{name,10}</c>, escaped.
    /// </summary>
    public void AppendFormatted<T>(T value, int alignment) => AppendFormatted(value, alignment, format: null);

    /// <summary>
    /// Adds a padded, formatted value, escaped.
    /// </summary>
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (value is RawHtml raw)
        {
            _html.Append(raw.Html);
            return;
        }

        var text = value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

        if (alignment != 0)
            text = alignment > 0 ? text.PadLeft(alignment) : text.PadRight(-alignment);

        _html.Append(WebUtility.HtmlEncode(text));
    }

    internal readonly string ToHtml() => _html?.ToString() ?? string.Empty;
}
