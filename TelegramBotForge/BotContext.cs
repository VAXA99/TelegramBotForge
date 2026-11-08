using Telegram.Bot;
using Telegram.Bot.Types;
using TelegramBotForge.Logging;
using TelegramBotForge.Media;
using TelegramBotForge.Payments;
using TelegramBotForge.State;

namespace TelegramBotForge;

/// <summary>
/// Everything about the update being processed. A new context is created for every update
/// and passed through the middleware, the router and the action.
/// </summary>
public sealed class BotContext
{
    internal BotContext(Update update, ITelegramBotClient bot, IServiceProvider services, CancellationToken cancellationToken)
    {
        Update = update;
        Bot = bot;
        Services = services;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the raw update from Telegram.
    /// </summary>
    public Update Update { get; }

    /// <summary>
    /// Gets the Telegram client. Actions answer with results instead of calling it directly.
    /// </summary>
    public ITelegramBotClient Bot { get; }

    /// <summary>
    /// Gets the services of this update. There is one DI scope per update.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// Gets the token that is cancelled when the bot stops.
    /// </summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Gets the new message of the update, if it is one.
    /// </summary>
    public Message? Message => Update.Message;

    /// <summary>
    /// Gets the button press of the update, if it is one.
    /// </summary>
    public CallbackQuery? CallbackQuery => Update.CallbackQuery;

    /// <summary>
    /// Gets the user who caused the update, if the update type has one.
    /// </summary>
    public User? From => UpdateDescriber.GetSender(Update);

    /// <summary>
    /// Gets the chat the update happened in, if the update type has one.
    /// </summary>
    public Chat? Chat => UpdateDescriber.GetChat(Update);

    /// <summary>
    /// Gets the id of <see cref="Chat"/>.
    /// </summary>
    public long? ChatId => Chat?.Id;

    /// <summary>
    /// Gets the id of <see cref="From"/>.
    /// </summary>
    public long? UserId => From?.Id;

    /// <summary>
    /// Gets the text of the new message, if it has one.
    /// </summary>
    public string? Text => Update.Message?.Text;

    /// <summary>
    /// Gets the command of the new message without the slash and the bot's @username, for example <c>start</c>
    /// for <c>/start@MyBot ref_42</c>; <c>null</c> when the message is not a command.
    /// </summary>
    public string? Command => CommandText.TryParse(Text, out var command, out _) ? command : null;

    /// <summary>
    /// Gets what follows the command, trimmed: <c>ref_42</c> for <c>/start ref_42</c>, which is also how
    /// a deep link <c>t.me/MyBot?start=ref_42</c> arrives. <c>null</c> when there is nothing after the command
    /// or the message is not a command. Bound to a <c>string args</c> or <c>string[] args</c> parameter.
    /// </summary>
    public string? CommandArgs => CommandText.TryParse(Text, out _, out var args) ? args : null;

    /// <summary>
    /// Gets a value indicating whether the message is a command for another bot, such as <c>/start@OtherBot</c> in a group.
    /// Such commands match no action.
    /// </summary>
    public bool IsCommandForAnotherBot =>
        BotUsername is { } me &&
        CommandText.TryParse(Text, out _, out _, out var mention) &&
        mention is not null &&
        !string.Equals(mention, me, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the bot's username, known once the bot has connected to Telegram.
    /// </summary>
    internal string? BotUsername { get; set; }

    /// <summary>
    /// Gets the data of the pressed button, if the update is a button press.
    /// </summary>
    public string? CallbackData => Update.CallbackQuery?.Data;

    /// <summary>
    /// Gets the photo, video, document or other media of the new message, if it has one.
    /// </summary>
    public BotMedia? Media
    {
        get
        {
            if (!_mediaRead)
            {
                _media = BotMedia.From(Update.Message);
                _mediaRead = true;
            }

            return _media;
        }
    }

    /// <summary>
    /// Gets the location the user shared, if the new message is one. Venues have a location too.
    /// </summary>
    public Location? Location => Update.Message?.Location;

    /// <summary>
    /// Gets the contact the user shared, if the new message is one.
    /// </summary>
    public Contact? Contact => Update.Message?.Contact;

    /// <summary>
    /// Gets the new version of a message the user edited, if the update is an edit.
    /// </summary>
    public Message? EditedMessage => Update.EditedMessage;

    /// <summary>
    /// Gets the change of the bot's own membership in a chat: blocked or unblocked by a user, added to or removed from a group.
    /// </summary>
    public ChatMemberUpdated? MembershipChange => Update.MyChatMember;

    /// <summary>
    /// Gets the inline query, if the update is one: a user typing <c>@YourBot something</c> in a chat.
    /// </summary>
    public InlineQuery? InlineQuery => Update.InlineQuery;

    /// <summary>
    /// Gets the items of an album, oldest first, when the bot has an <c>[Album]</c> action and the update is an album;
    /// otherwise <c>null</c>. <see cref="Update"/> and <see cref="Message"/> are those of the first item.
    /// </summary>
    public IReadOnlyList<BotMedia>? Album => _album ??= AlbumMessages?.Select(BotMedia.From).OfType<BotMedia>().ToArray();

    /// <summary>
    /// Gets the messages of an album, oldest first, or <c>null</c>; see <see cref="Album"/>.
    /// </summary>
    public IReadOnlyList<Message>? AlbumMessages { get; internal set; }

    /// <summary>
    /// Gets the order being checked before payment, set by the Stars middleware. Bound to <c>StarsOrder</c> parameters.
    /// </summary>
    public StarsOrder? StarsOrder { get; internal set; }

    /// <summary>
    /// Gets the completed payment, set by the Stars middleware. Bound to <c>StarsPayment</c> parameters.
    /// </summary>
    public StarsPayment? StarsPayment { get; internal set; }

    /// <summary>
    /// Gets the conversation state of the chat and user. Loaded before routing; <c>null</c> for updates without a chat or user.
    /// </summary>
    public ChatState? State { get; internal set; }

    /// <summary>
    /// Gets a bag for middleware to pass data to actions, for example a loaded user record.
    /// </summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    /// <summary>
    /// Gets or sets a value indicating whether the button press was already answered, so it is not answered twice.
    /// </summary>
    internal bool CallbackAnswered { get; set; }

    /// <summary>
    /// Gets or sets the action that processed the update, set by the router.
    /// </summary>
    internal string? HandledBy { get; set; }

    /// <summary>
    /// Gets or sets the culture of the user, set by <c>UseLocalization()</c>; <c>null</c> without it.
    /// </summary>
    public System.Globalization.CultureInfo? Culture { get; internal set; }

    private IReadOnlyList<BotMedia>? _album;
    private BotMedia? _media;
    private bool _mediaRead;
}

/// <summary>
/// Splits <c>/name@bot arguments</c> into its parts.
/// </summary>
internal static class CommandText
{
    private static readonly char[] Separators = [' ', '\n', '\t'];

    public static bool TryParse(string? text, out string command, out string? args) => TryParse(text, out command, out args, out _);

    /// <param name="text">The message text.</param>
    /// <param name="command">The command without the slash.</param>
    /// <param name="args">What follows the command, or <c>null</c>.</param>
    /// <param name="mention">The bot's username after <c>@</c>, as in <c>/start@MyBot</c>; <c>null</c> without one.</param>
    public static bool TryParse(string? text, out string command, out string? args, out string? mention)
    {
        command = string.Empty;
        args = null;
        mention = null;
        if (string.IsNullOrEmpty(text) || text[0] != '/' || text.Length == 1)
            return false;

        var end = text.IndexOfAny(Separators);
        var head = end < 0 ? text[1..] : text[1..end];
        var at = head.IndexOf('@');
        command = at < 0 ? head : head[..at];
        if (at >= 0 && at + 1 < head.Length)
            mention = head[(at + 1)..];
        if (command.Length == 0)
            return false;

        if (end >= 0)
        {
            var rest = text[(end + 1)..].Trim();
            args = rest.Length == 0 ? null : rest;
        }

        return true;
    }

    /// <summary>
    /// Splits the arguments at whitespace; an empty array when there are none.
    /// </summary>
    public static string[] Split(string? args) =>
        string.IsNullOrEmpty(args) ? [] : args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
