using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Media;

namespace TelegramBotForge.Views;

/// <summary>
/// Describes a message: its lines, media and keyboard. A view gets it in <c>Build</c>; the limits Telegram enforces
/// (text length, button data, buttons per row, one kind of keyboard) are checked when the view is rendered,
/// with an error that names the view.
/// </summary>
public sealed class MessageBuilder
{
    private const int MaxTextLength = 4096;
    private const int MaxCaptionLength = 1024;
    private const int MaxButtonsPerRow = 8;
    private const int MaxInlineButtons = 100;

    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

    private readonly string _viewName;
    private readonly bool _rich;
    private readonly Action<PaginationOptions> _configurePagination;
    private readonly List<string> _lines = [];
    private readonly List<IReadOnlyList<BotButton>> _inlineRows = [];
    private readonly List<IReadOnlyList<KeyboardButton>> _replyRows = [];
    private KeyboardKind _keyboard;
    private MediaFile? _media;
    private bool _linkPreview;

    internal MessageBuilder(string viewName, bool rich, Action<PaginationOptions> configurePagination)
    {
        _viewName = viewName;
        _rich = rich;
        _configurePagination = configurePagination;
    }

    private enum KeyboardKind
    {
        None,
        Inline,
        Reply,
        Remove
    }

    // ===================== Text =====================

    /// <summary>
    /// Adds a line written as an interpolated string: the fixed parts are HTML and every <c>{value}</c> is escaped,
    /// for example <c>.Line($"&lt;b&gt;Name:&lt;/b&gt; {user.Name}")</c>.
    /// </summary>
    public MessageBuilder Line(HtmlText line)
    {
        _lines.Add(line.ToHtml());
        return this;
    }

    /// <summary>
    /// Adds a line of plain text, escaped completely, for example text a user wrote.
    /// </summary>
    public MessageBuilder Text(string? text)
    {
        _lines.Add(WebUtility.HtmlEncode(text ?? string.Empty));
        return this;
    }

    /// <summary>
    /// Adds a line of HTML as it is, without escaping. Only for HTML you built yourself.
    /// </summary>
    public MessageBuilder Line(RawHtml html)
    {
        _lines.Add(html.Html);
        return this;
    }

    /// <summary>
    /// Adds an empty line.
    /// </summary>
    public MessageBuilder EmptyLine()
    {
        _lines.Add(string.Empty);
        return this;
    }

    /// <summary>
    /// Shows a preview of the first link in the text. Off by default.
    /// </summary>
    public MessageBuilder ShowLinkPreview()
    {
        _linkPreview = true;
        return this;
    }

    // ===================== Media =====================

    /// <summary>
    /// Shows a photo, video or other media; the lines become its caption (up to 1024 characters).
    /// </summary>
    public MessageBuilder Media(MediaFile media)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (_rich)
            throw Problem("rich messages cannot have media.");
        _media = media;
        return this;
    }

    // ===================== Inline buttons =====================

    /// <summary>
    /// Adds a row of buttons, up to 8.
    /// </summary>
    public MessageBuilder Row(params BotButton[] buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        AddInlineRow(buttons);
        return this;
    }

    /// <summary>
    /// Adds a button for every item, <paramref name="perRow"/> buttons per row.
    /// </summary>
    public MessageBuilder Buttons<T>(IEnumerable<T> items, Func<T, BotButton> button, int perRow = 1)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(button);
        ArgumentOutOfRangeException.ThrowIfLessThan(perRow, 1);

        foreach (var row in items.Select(button).Chunk(perRow))
            AddInlineRow(row);
        return this;
    }

    /// <summary>
    /// Adds a pagination row, <c>« | 2 / 5 | »</c> by default.
    /// </summary>
    /// <param name="page">The current page, starting at 1. Out-of-range values are moved into range.</param>
    /// <param name="totalPages">The number of pages.</param>
    /// <param name="route">The button data for a page, for example <c>p => $"products:{p}"</c>.</param>
    /// <param name="configure">Overrides for this call; see <see cref="PaginationOptions"/>.</param>
    public MessageBuilder Pagination(int page, int totalPages, Func<int, string> route, Action<PaginationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(route);

        var options = CreatePaginationOptions(configure);
        var total = Math.Max(1, totalPages);
        var info = new PageInfo(Math.Clamp(page, 1, total), total, route, options, _rich);

        foreach (var row in options.BuildRows(info))
            AddInlineRow(row);
        return this;
    }

    /// <summary>
    /// Shows one page of a list: a button for each item of the page, then the pagination row.
    /// </summary>
    /// <param name="items">All items.</param>
    /// <param name="page">The current page, starting at 1. Out-of-range values are moved into range.</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="item">The button of an item.</param>
    /// <param name="route">The button data for a page, for example <c>p => $"products:{p}"</c>.</param>
    /// <param name="perRow">Item buttons per row.</param>
    /// <param name="configure">Overrides for the pagination row; see <see cref="PaginationOptions"/>.</param>
    public MessageBuilder Page<T>(
        IReadOnlyList<T> items,
        int page,
        int pageSize,
        Func<T, BotButton> item,
        Func<int, string> route,
        int perRow = 1,
        Action<PaginationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var totalPages = Math.Max(1, (items.Count + pageSize - 1) / pageSize);
        var current = Math.Clamp(page, 1, totalPages);

        Buttons(items.Skip((current - 1) * pageSize).Take(pageSize), item, perRow);
        return Pagination(current, totalPages, route, configure);
    }

    /// <summary>
    /// Adds a back button, "« Back" by default.
    /// </summary>
    public MessageBuilder Back(string data, string? text = null) =>
        Row(TelegramBotForge.Views.Buttons.Callback(text ?? ViewDefaults.Current.BackText, data));

    /// <summary>
    /// Adds back and next buttons in one row. Leave one out to show only the other.
    /// </summary>
    public MessageBuilder Navigation(string? back = null, string? next = null, string? backText = null, string? nextText = null)
    {
        var row = new List<BotButton>();
        if (back is not null)
            row.Add(TelegramBotForge.Views.Buttons.Callback(backText ?? ViewDefaults.Current.BackText, back));
        if (next is not null)
            row.Add(TelegramBotForge.Views.Buttons.Callback(nextText ?? ViewDefaults.Current.NextText, next));

        if (row.Count == 0)
            throw new ArgumentException("Navigation needs a back or a next button.");

        AddInlineRow(row);
        return this;
    }

    /// <summary>
    /// Adds confirm and cancel buttons in one row, "✅ Confirm | ✖ Cancel" by default.
    /// </summary>
    public MessageBuilder Confirm(string yes, string no, string? yesText = null, string? noText = null) => Row(
        TelegramBotForge.Views.Buttons.Callback(yesText ?? ViewDefaults.Current.ConfirmText, yes),
        TelegramBotForge.Views.Buttons.Callback(noText ?? ViewDefaults.Current.CancelText, no));

    // ===================== Reply keyboard =====================

    /// <summary>
    /// Shows answer buttons under the input field; pressing one sends its text as a message.
    /// </summary>
    public MessageBuilder ReplyButtons(IEnumerable<string> texts, int perRow = 1)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentOutOfRangeException.ThrowIfLessThan(perRow, 1);

        foreach (var row in texts.Chunk(perRow))
            AddReplyRow(row.Select(text => new KeyboardButton(text)).ToArray());
        return this;
    }

    /// <summary>
    /// Shows a button under the input field that shares the user's verified phone number. Private chats only.
    /// </summary>
    public MessageBuilder RequestContact(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        AddReplyRow([KeyboardButton.WithRequestContact(text)]);
        return this;
    }

    /// <summary>
    /// Shows a button under the input field that sends the user's location. Private chats only.
    /// </summary>
    public MessageBuilder RequestLocation(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        AddReplyRow([KeyboardButton.WithRequestLocation(text)]);
        return this;
    }

    /// <summary>
    /// Removes the buttons under the input field.
    /// </summary>
    public MessageBuilder RemoveKeyboard()
    {
        UseKeyboard(KeyboardKind.Remove);
        return this;
    }

    // ===================== Build =====================

    /// <summary>
    /// Gets a value indicating whether nothing was added: no text, media or keyboard.
    /// </summary>
    internal bool IsEmpty =>
        _media is null && _keyboard == KeyboardKind.None && _lines.All(string.IsNullOrWhiteSpace);

    internal BotMessage Build()
    {
        var text = string.Join("\n", _lines);

        if (_rich)
            text += RenderRichRows();
        else
            CheckLength(text);

        if (text.Length == 0 && _media is null)
            throw Problem("the message has no text. Add a line or media.");

        ReplyMarkup? keyboard = _keyboard switch
        {
            KeyboardKind.Inline when !_rich => new InlineKeyboardMarkup(_inlineRows.Select(row => row.Select(ToInlineButton))),
            KeyboardKind.Reply => new ReplyKeyboardMarkup(_replyRows) { ResizeKeyboard = true, OneTimeKeyboard = true },
            KeyboardKind.Remove => new ReplyKeyboardRemove(),
            _ => null
        };

        return new BotMessage(text, keyboard, ParseMode.Html, !_linkPreview, _media, _rich);
    }

    private PaginationOptions CreatePaginationOptions(Action<PaginationOptions>? configure)
    {
        var options = ViewDefaults.Current.CreatePaginationOptions();
        _configurePagination(options);
        configure?.Invoke(options);
        return options;
    }

    private void AddInlineRow(IReadOnlyCollection<BotButton> row)
    {
        if (row.Count == 0)
            return;
        if (row.Count > MaxButtonsPerRow)
            throw Problem($"a row has {row.Count} buttons; Telegram allows {MaxButtonsPerRow}.");

        UseKeyboard(KeyboardKind.Inline);

        foreach (var button in row)
        {
            if (!_rich && button.IsDisabled)
                throw Problem($"button \"{button.Text}\" is disabled, but disabled buttons only exist in rich messages ([View(Rich = true)]).");
            if (_rich && button.Kind != ButtonKind.Callback)
                throw Problem($"button \"{button.Text}\" is a {button.Kind} button; rich messages only support callback and disabled buttons.");
        }

        _inlineRows.Add(row.ToArray());
        if (_inlineRows.Sum(r => r.Count) > MaxInlineButtons)
            throw Problem($"the message has more than {MaxInlineButtons} buttons.");
    }

    private void AddReplyRow(IReadOnlyList<KeyboardButton> row)
    {
        if (row.Count == 0)
            return;
        if (_rich)
            throw Problem("rich messages cannot have buttons under the input field.");

        UseKeyboard(KeyboardKind.Reply);
        _replyRows.Add(row);
    }

    private void UseKeyboard(KeyboardKind kind)
    {
        if (_keyboard != KeyboardKind.None && _keyboard != kind)
        {
            throw Problem($"it mixes {Describe(_keyboard)} and {Describe(kind)}. Telegram allows only one kind of keyboard per message.");
        }

        _keyboard = kind;

        static string Describe(KeyboardKind kind) => kind switch
        {
            KeyboardKind.Inline => "buttons under the message",
            KeyboardKind.Reply => "buttons under the input field",
            _ => "a keyboard removal"
        };
    }

    private void CheckLength(string html)
    {
        // Telegram counts the visible text, without tags and with entities decoded.
        var visible = WebUtility.HtmlDecode(Tags.Replace(html, string.Empty)).Length;
        var limit = _media is null ? MaxTextLength : MaxCaptionLength;
        if (visible > limit)
            throw Problem($"the {(_media is null ? "text" : "caption")} is {visible} characters; Telegram allows {limit}.");
    }

    private string RenderRichRows()
    {
        if (_inlineRows.Count == 0)
            return string.Empty;

        var html = new StringBuilder("\n");
        foreach (var row in _inlineRows)
        {
            html.Append("<tg-button-row>");
            foreach (var button in row)
                html.Append(ToRichButton(button));
            html.Append("</tg-button-row>");
        }

        return html.ToString();
    }

    private static string ToRichButton(BotButton button)
    {
        var content = WebUtility.HtmlEncode(button.Text);
        if (button.IconId is not null)
        {
            content = $"<tg-emoji emoji-id=\"{WebUtility.HtmlEncode(button.IconId)}\">{WebUtility.HtmlEncode(button.IconFallback)}</tg-emoji> {content}";
        }

        if (button.IsDisabled)
            return $"<tg-button type=\"disabled\">{content}</tg-button>";

        var style = button.Appearance switch
        {
            ButtonStyle.Primary => " style=\"primary\"",
            ButtonStyle.Success => " style=\"success\"",
            ButtonStyle.Danger => " style=\"danger\"",
            _ => string.Empty
        };

        return $"<tg-button type=\"callback_data\"{style} data=\"{WebUtility.HtmlEncode(button.Value)}\">{content}</tg-button>";
    }

    private static InlineKeyboardButton ToInlineButton(BotButton button)
    {
        var inline = button.Kind switch
        {
            ButtonKind.Url => InlineKeyboardButton.WithUrl(button.Text, button.Value),
            ButtonKind.Copy => new InlineKeyboardButton(button.Text) { CopyText = new CopyTextButton { Text = button.Value } },
            _ => InlineKeyboardButton.WithCallbackData(button.Text, button.Value)
        };

        switch (button.Appearance)
        {
            case ButtonStyle.Primary: inline.Style = KeyboardButtonStyle.Primary; break;
            case ButtonStyle.Success: inline.Style = KeyboardButtonStyle.Success; break;
            case ButtonStyle.Danger: inline.Style = KeyboardButtonStyle.Danger; break;
        }

        if (button.IconId is not null)
            inline.IconCustomEmojiId = button.IconId;

        return inline;
    }

    private InvalidOperationException Problem(string message) => new($"View {_viewName}: {message}");
}
