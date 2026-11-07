using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Media;
using TelegramBotForge.Results;
using TelegramBotForge.State;
using TelegramBotForge.Views;

namespace TelegramBotForge.Controllers;

/// <summary>
/// The base class of controllers. A controller groups related actions: public methods with a route attribute
/// such as <see cref="CommandAttribute"/> or <see cref="CallbackAttribute"/>.
/// A new controller is created for every update, so services can come through the constructor.
/// </summary>
/// <example>
/// <code>
/// public class ShopController(IProductService products, ProductCardView card) : BotController
/// {
///     [Callback("product:{id:int}")]
///     public async Task&lt;IBotResult&gt; Open(int id)
///     {
///         var product = await products.GetAsync(id);
///         return product is null ? Answer("Product not found.") : Edit(card.Render(product));
///     }
/// }
/// </code>
/// </example>
public abstract class BotController
{
    /// <summary>
    /// Gets the context of the update being processed.
    /// </summary>
    public BotContext Context { get; internal set; } = null!;

    /// <summary>
    /// Gets the conversation state of the user in this chat.
    /// </summary>
    /// <exception cref="InvalidOperationException">The update has no chat or user, so it has no state.</exception>
    protected ChatState State => Context.State
        ?? throw new InvalidOperationException("This update has no chat and user, so it has no conversation state.");

    /// <summary>
    /// Sends a new message.
    /// </summary>
    protected static IBotResult Reply(string text, ReplyMarkup? keyboard = null) => new SendText(text, keyboard);

    /// <summary>
    /// Sends a rendered view as a new message, with its media if it has one.
    /// </summary>
    protected static IBotResult Reply(BotMessage message) =>
        message.Media is null ? SendText.From(message) : SendMedia.From(message);

    /// <summary>
    /// Sends a photo, video, document or other media, for example <c>ReplyMedia(MediaFile.From(media), "Got it")</c>.
    /// </summary>
    protected static IBotResult ReplyMedia(MediaFile media, string? caption = null, ReplyMarkup? keyboard = null) =>
        new SendMedia(media, caption, keyboard);

    /// <summary>
    /// Edits the message whose button was pressed, or sends a new message when there is none.
    /// </summary>
    protected static IBotResult Edit(string text, InlineKeyboardMarkup? keyboard = null) => new EditText(text, keyboard);

    /// <summary>
    /// Shows a rendered view in the message whose button was pressed, or sends it as a new message when there is none
    /// or when Telegram cannot edit it that way (for example a text message into a photo).
    /// </summary>
    protected static IBotResult Edit(BotMessage message) =>
        message.Media is null ? EditText.From(message) : EditMedia.From(message);

    /// <summary>
    /// Answers the pressed button, optionally with a notification or an alert.
    /// </summary>
    protected static IBotResult Answer(string? text = null, bool alert = false) => new AnswerCallback(text, alert);

    /// <summary>
    /// Deletes the message whose button was pressed, or the user's message.
    /// </summary>
    protected static IBotResult Delete() => new DeleteMessage();

    /// <summary>
    /// Sends an invoice for a Stars product, for example <c>Invoice("premium_month")</c>.
    /// </summary>
    protected static IBotResult Invoice(string productCode) => new StarsInvoice(productCode);

    /// <summary>
    /// Lets the payment go ahead. For <c>[PreCheckout]</c> actions.
    /// </summary>
    protected static IBotResult AcceptCheckout() => AnswerPreCheckout.Accept;

    /// <summary>
    /// Declines the order with a reason the user sees. For <c>[PreCheckout]</c> actions.
    /// </summary>
    protected static IBotResult DeclineCheckout(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new AnswerPreCheckout(false, reason);
    }

    /// <summary>
    /// Gives the Stars of a payment back, by default to the user of the update.
    /// </summary>
    protected static IBotResult Refund(string chargeId, long? userId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chargeId);
        return new RefundStars(chargeId, userId);
    }

    /// <summary>
    /// Moves the user to <paramref name="state"/> and sends a message, typically the next question of a form.
    /// </summary>
    protected IBotResult EnterState(Enum state, string text, ReplyMarkup? keyboard = null) =>
        EnterState(state, Reply(text, keyboard));

    /// <summary>
    /// Moves the user to <paramref name="state"/> and sends a rendered view.
    /// </summary>
    protected IBotResult EnterState(Enum state, BotMessage message) => EnterState(state, Reply(message));

    /// <summary>
    /// Moves the user to <paramref name="state"/> and returns <paramref name="result"/>. The collected data is kept.
    /// </summary>
    protected IBotResult EnterState(Enum state, IBotResult result)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(result);
        State.Current = StateNames.Of(state);
        return result;
    }

    /// <summary>
    /// Ends the conversation (clears the state and its data) and sends a message.
    /// </summary>
    protected IBotResult ExitState(string text, ReplyMarkup? keyboard = null) => ExitState(Reply(text, keyboard));

    /// <summary>
    /// Ends the conversation (clears the state and its data) and returns <paramref name="result"/>.
    /// </summary>
    protected IBotResult ExitState(IBotResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ExitState();
        return result;
    }

    /// <summary>
    /// Ends the conversation: clears the state and its data.
    /// </summary>
    protected void ExitState() => Context.State?.Clear();
}
