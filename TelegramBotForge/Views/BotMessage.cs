using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Media;

namespace TelegramBotForge.Views;

/// <summary>
/// A rendered message: what a view produces. The action decides how it is delivered,
/// for example <c>Reply(view.Render(model))</c> or <c>Edit(view.Render(model))</c>.
/// </summary>
/// <param name="Text">The text. With <paramref name="Media"/>, it is the caption (Telegram allows up to 1024 characters).</param>
/// <param name="Keyboard">An inline keyboard, a reply keyboard, or a keyboard removal. Edits only accept inline keyboards.</param>
/// <param name="ParseMode">How the text is formatted. HTML by default.</param>
/// <param name="DisableLinkPreview">Turns off link previews. On by default.</param>
/// <param name="Media">A photo, video or other media to show, with <paramref name="Text"/> as its caption.</param>
/// <param name="Rich">The text is a Telegram rich message, with its buttons inside the HTML.</param>
public sealed record BotMessage(
    string Text,
    ReplyMarkup? Keyboard = null,
    ParseMode ParseMode = ParseMode.Html,
    bool DisableLinkPreview = true,
    MediaFile? Media = null,
    bool Rich = false);
