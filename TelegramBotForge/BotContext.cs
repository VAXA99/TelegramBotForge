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

    private BotMedia? _media;
    private bool _mediaRead;
}
