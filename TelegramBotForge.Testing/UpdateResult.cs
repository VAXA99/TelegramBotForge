using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;

namespace TelegramBotForge.Testing;

/// <summary>
/// What the bot did for one update: the Telegram requests it made, in order.
/// </summary>
public sealed class UpdateResult
{
    internal UpdateResult(IReadOnlyList<IRequest> requests) => Requests = requests;

    /// <summary>
    /// Gets every request the bot made for the update.
    /// </summary>
    public IReadOnlyList<IRequest> Requests { get; }

    /// <summary>
    /// Gets the texts the bot sent or edited, in order.
    /// </summary>
    public IReadOnlyList<string> Texts => Requests
        .Select(request => request switch
        {
            SendMessageRequest send => send.Text,
            EditMessageTextRequest edit => edit.Text ?? edit.RichMessage?.Html,
            SendRichMessageRequest rich => rich.RichMessage?.Html,
            _ => null
        })
        .Where(text => text is not null)
        .ToList()!;

    /// <summary>
    /// Gets the only text the bot sent or edited. Throws when there was none or more than one, so a test fails clearly.
    /// </summary>
    public string Text => Texts.Count == 1
        ? Texts[0]
        : throw new InvalidOperationException($"Expected one text from the bot, but it sent {Texts.Count}: " +
                                              string.Join(" | ", Texts.Select(text => $"\"{text}\"")));

    /// <summary>
    /// Gets a value indicating whether the bot did nothing.
    /// </summary>
    public bool IsEmpty => Requests.Count == 0;

    /// <summary>
    /// Gets the requests of one type, for example <c>Of&lt;SendInvoiceRequest&gt;()</c>.
    /// </summary>
    public IReadOnlyList<TRequest> Of<TRequest>() where TRequest : IRequest => Requests.OfType<TRequest>().ToList();

    /// <summary>
    /// Gets the answer to the button press, or <c>null</c> when it was not answered.
    /// </summary>
    public AnswerCallbackQueryRequest? CallbackAnswer => Of<AnswerCallbackQueryRequest>().LastOrDefault();

    /// <summary>
    /// Gets the answer to the check before payment, or <c>null</c> when there was none.
    /// </summary>
    public AnswerPreCheckoutQueryRequest? CheckoutAnswer => Of<AnswerPreCheckoutQueryRequest>().LastOrDefault();

    /// <summary>
    /// Gets the answer to the inline query, or <c>null</c> when there was none.
    /// </summary>
    public AnswerInlineQueryRequest? InlineAnswer => Of<AnswerInlineQueryRequest>().LastOrDefault();

    /// <inheritdoc/>
    public override string ToString() => string.Join(", ", Requests.Select(request => request.MethodName));
}
