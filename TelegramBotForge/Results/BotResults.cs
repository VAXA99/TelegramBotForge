using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Media;
using TelegramBotForge.Views;

namespace TelegramBotForge.Results;

/// <summary>
/// What an action or middleware wants to happen. Results only describe the answer;
/// <see cref="IBotResultExecutor"/> sends them to Telegram after the pipeline has finished.
/// </summary>
public interface IBotResult
{
}

/// <summary>
/// Sends a new text message to the chat of the update.
/// </summary>
/// <param name="Text">The text.</param>
/// <param name="Keyboard">An inline keyboard, a reply keyboard, or a keyboard removal.</param>
/// <param name="ParseMode">How the text is formatted. HTML by default.</param>
/// <param name="DisableLinkPreview">Turns off link previews. On by default.</param>
/// <param name="Rich">The text is a Telegram rich message, with its buttons inside the HTML.</param>
public sealed record SendText(
    string Text,
    ReplyMarkup? Keyboard = null,
    ParseMode ParseMode = ParseMode.Html,
    bool DisableLinkPreview = true,
    bool Rich = false) : IBotResult
{
    /// <summary>
    /// Creates the result from a rendered view without media.
    /// </summary>
    /// <exception cref="ArgumentException">The view has media; use <see cref="SendMedia.From"/>.</exception>
    public static SendText From(BotMessage message) => message.Media is null
        ? new(message.Text, message.Keyboard, message.ParseMode, message.DisableLinkPreview, message.Rich)
        : throw new ArgumentException("The message has media; use SendMedia.From.", nameof(message));
}

/// <summary>
/// Sends a photo, video, document or other media to the chat of the update.
/// </summary>
/// <param name="Media">The media and where its file comes from.</param>
/// <param name="Caption">The caption, up to 1024 characters. Video notes and stickers cannot have one.</param>
/// <param name="Keyboard">An inline keyboard, a reply keyboard, or a keyboard removal.</param>
/// <param name="ParseMode">How the caption is formatted. HTML by default.</param>
public sealed record SendMedia(
    MediaFile Media,
    string? Caption = null,
    ReplyMarkup? Keyboard = null,
    ParseMode ParseMode = ParseMode.Html) : IBotResult
{
    /// <summary>
    /// Creates the result from a rendered view with media; its text becomes the caption.
    /// </summary>
    /// <exception cref="ArgumentException">The view has no media.</exception>
    public static SendMedia From(BotMessage message) => new(
        message.Media ?? throw new ArgumentException("The message has no media; use SendText.From.", nameof(message)),
        string.IsNullOrEmpty(message.Text) ? null : message.Text,
        message.Keyboard,
        message.ParseMode);
}

/// <summary>
/// Edits a text message: by default the one whose button was pressed.
/// When there is no message to edit, or it can no longer be edited, a new message is sent instead.
/// </summary>
/// <param name="Text">The new text.</param>
/// <param name="Keyboard">The new inline keyboard. <c>null</c> removes it.</param>
/// <param name="ParseMode">How the text is formatted. HTML by default.</param>
/// <param name="DisableLinkPreview">Turns off link previews. On by default.</param>
/// <param name="MessageId">The message to edit. By default the message of the pressed button.</param>
/// <param name="Rich">The text is a Telegram rich message, with its buttons inside the HTML.</param>
public sealed record EditText(
    string Text,
    InlineKeyboardMarkup? Keyboard = null,
    ParseMode ParseMode = ParseMode.Html,
    bool DisableLinkPreview = true,
    int? MessageId = null,
    bool Rich = false) : IBotResult
{
    /// <summary>
    /// Creates the result from a rendered view.
    /// </summary>
    /// <exception cref="ArgumentException">The view has a reply keyboard; edited messages can only have inline keyboards.</exception>
    public static EditText From(BotMessage message) => message.Media is not null
        ? throw new ArgumentException("The message has media; use EditMedia.From.", nameof(message))
        : message.Keyboard switch
    {
        null => new(message.Text, null, message.ParseMode, message.DisableLinkPreview, Rich: message.Rich),
        InlineKeyboardMarkup inline => new(message.Text, inline, message.ParseMode, message.DisableLinkPreview, Rich: message.Rich),
        _ => throw new ArgumentException("An edited message can only have an inline keyboard.", nameof(message))
    };
}

/// <summary>
/// Replaces the media of a message: by default the one whose button was pressed. Telegram cannot turn a text message
/// into a media message or edit voice messages, video notes and stickers; in those cases a new message is sent instead.
/// </summary>
/// <param name="Media">The new media.</param>
/// <param name="Caption">The new caption, up to 1024 characters.</param>
/// <param name="Keyboard">The new inline keyboard. <c>null</c> removes it.</param>
/// <param name="ParseMode">How the caption is formatted. HTML by default.</param>
/// <param name="MessageId">The message to edit. By default the message of the pressed button.</param>
public sealed record EditMedia(
    MediaFile Media,
    string? Caption = null,
    InlineKeyboardMarkup? Keyboard = null,
    ParseMode ParseMode = ParseMode.Html,
    int? MessageId = null) : IBotResult
{
    /// <summary>
    /// Creates the result from a rendered view with media; its text becomes the caption.
    /// </summary>
    /// <exception cref="ArgumentException">The view has no media, or a reply keyboard.</exception>
    public static EditMedia From(BotMessage message)
    {
        var media = message.Media ?? throw new ArgumentException("The message has no media; use EditText.From.", nameof(message));
        var caption = string.IsNullOrEmpty(message.Text) ? null : message.Text;
        return message.Keyboard switch
        {
            null => new(media, caption, null, message.ParseMode),
            InlineKeyboardMarkup inline => new(media, caption, inline, message.ParseMode),
            _ => throw new ArgumentException("An edited message can only have an inline keyboard.", nameof(message))
        };
    }
}

/// <summary>
/// Answers the pressed button: stops its loading spinner and optionally shows a short notification or an alert.
/// </summary>
/// <param name="Text">The notification text, or <c>null</c> for none.</param>
/// <param name="ShowAlert">Shows the text as an alert the user has to close, instead of a notification.</param>
public sealed record AnswerCallback(string? Text = null, bool ShowAlert = false) : IBotResult;

/// <summary>
/// Deletes a message: by default the one whose button was pressed, or the user's message.
/// </summary>
/// <param name="MessageId">The message to delete.</param>
public sealed record DeleteMessage(int? MessageId = null) : IBotResult;

/// <summary>
/// Several results, executed in order.
/// </summary>
public sealed class MultiResult : IBotResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MultiResult"/> class.
    /// </summary>
    public MultiResult(params IBotResult[] results) => Results = results;

    /// <summary>
    /// Gets the results in the order they are executed.
    /// </summary>
    public IReadOnlyList<IBotResult> Results { get; }
}

/// <summary>
/// Combines results.
/// </summary>
public static class BotResultExtensions
{
    /// <summary>
    /// Executes <paramref name="next"/> after <paramref name="first"/>, for example
    /// <c>Answer("Saved").Then(Edit("..."))</c>.
    /// </summary>
    public static IBotResult Then(this IBotResult first, IBotResult next)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(next);

        var results = new List<IBotResult>();
        Add(results, first);
        Add(results, next);
        return new MultiResult(results.ToArray());

        static void Add(List<IBotResult> results, IBotResult result)
        {
            if (result is MultiResult multi)
                results.AddRange(multi.Results);
            else
                results.Add(result);
        }
    }
}
