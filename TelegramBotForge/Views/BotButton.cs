using System.Text;

namespace TelegramBotForge.Views;

/// <summary>
/// What a button does when it is pressed.
/// </summary>
public enum ButtonKind
{
    /// <summary>Sends its data to the bot; reaches a <c>[Callback]</c> action.</summary>
    Callback,

    /// <summary>Opens a link.</summary>
    Url,

    /// <summary>Copies a text to the user's clipboard.</summary>
    Copy
}

/// <summary>
/// The color of a button.
/// </summary>
public enum ButtonStyle
{
    Default,
    Primary,
    Success,
    Danger
}

/// <summary>
/// A button of a view. Create it with <c>Button</c>, <c>UrlButton</c> or <c>CopyButton</c> inside a view,
/// or with <see cref="Buttons"/> anywhere else, and change it with <see cref="Style"/>, <see cref="Icon"/> and <see cref="Disabled"/>.
/// </summary>
public sealed record BotButton
{
    internal BotButton(ButtonKind kind, string text, string value)
    {
        Kind = kind;
        Text = text;
        Value = value;
    }

    /// <summary>
    /// Gets what the button does.
    /// </summary>
    public ButtonKind Kind { get; }

    /// <summary>
    /// Gets the label. Labels are plain text: Telegram does not format them.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets the callback data, the URL or the text to copy, depending on <see cref="Kind"/>.
    /// </summary>
    public string Value { get; }

    internal ButtonStyle Appearance { get; init; }

    internal string? IconId { get; init; }

    internal string? IconFallback { get; init; }

    internal bool IsDisabled { get; init; }

    /// <summary>
    /// Gives the button a color, for example <c>Button("Delete", "item:delete:42").Style(ButtonStyle.Danger)</c>.
    /// </summary>
    public BotButton Style(ButtonStyle style) => this with { Appearance = style };

    /// <summary>
    /// Shows a custom emoji before the label.
    /// </summary>
    /// <param name="customEmojiId">The custom emoji id.</param>
    /// <param name="fallback">A regular emoji for clients that cannot show custom emoji in rich messages.</param>
    public BotButton Icon(string customEmojiId, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customEmojiId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        return this with { IconId = customEmojiId, IconFallback = fallback };
    }

    /// <summary>
    /// Makes the button visible but not pressable. Only rich messages (<c>[View(Rich = true)]</c>) have disabled buttons;
    /// a normal message with one fails when the view is rendered.
    /// </summary>
    public BotButton Disabled() => this with { IsDisabled = true };
}

/// <summary>
/// Creates buttons outside a view. Inside a view, use its <c>Button</c>, <c>UrlButton</c> and <c>CopyButton</c>.
/// </summary>
public static class Buttons
{
    /// <summary>
    /// The data of buttons that only show something, such as the page counter. No action handles it,
    /// so pressing such a button only stops its loading spinner.
    /// </summary>
    public const string NoopData = "tbf:noop";

    private const int MaxCallbackDataBytes = 64;

    /// <summary>
    /// A button that sends <paramref name="data"/> to the bot. Without data, pressing it does nothing.
    /// </summary>
    /// <exception cref="ArgumentException">The data is longer than Telegram's 64 bytes.</exception>
    public static BotButton Callback(string text, string? data = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        data ??= NoopData;

        var bytes = Encoding.UTF8.GetByteCount(data);
        if (bytes is < 1 or > MaxCallbackDataBytes)
            throw new ArgumentException($"The data of button \"{text}\" is {bytes} bytes; Telegram allows 1-{MaxCallbackDataBytes}: \"{data}\".", nameof(data));

        return new BotButton(ButtonKind.Callback, text, data);
    }

    /// <summary>
    /// A button that opens a link.
    /// </summary>
    public static BotButton Url(string text, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            throw new ArgumentException($"The link of button \"{text}\" is not an absolute URL: \"{url}\".", nameof(url));
        return new BotButton(ButtonKind.Url, text, url);
    }

    /// <summary>
    /// A button that copies <paramref name="textToCopy"/> to the user's clipboard.
    /// </summary>
    public static BotButton Copy(string text, string textToCopy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(textToCopy);
        return new BotButton(ButtonKind.Copy, text, textToCopy);
    }
}
