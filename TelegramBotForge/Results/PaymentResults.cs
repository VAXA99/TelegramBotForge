namespace TelegramBotForge.Results;

/// <summary>
/// Sends an invoice for a Stars product to the chat of the update. The invoice has Telegram's own "Pay ⭐" button.
/// </summary>
/// <param name="ProductCode">The code of the product, as given to <c>AddStars</c>.</param>
public sealed record StarsInvoice(string ProductCode) : IBotResult;

/// <summary>
/// Answers the check before payment. A declined order shows <paramref name="ErrorMessage"/> to the user.
/// </summary>
/// <param name="Ok">Whether the payment may go ahead.</param>
/// <param name="ErrorMessage">Why the order was declined; required when <paramref name="Ok"/> is <c>false</c>.</param>
public sealed record AnswerPreCheckout(bool Ok, string? ErrorMessage = null) : IBotResult
{
    /// <summary>
    /// Lets the payment go ahead.
    /// </summary>
    public static AnswerPreCheckout Accept { get; } = new(true);
}

/// <summary>
/// Gives the Stars of a payment back to the user.
/// </summary>
/// <param name="ChargeId">The <c>ChargeId</c> of the payment.</param>
/// <param name="UserId">The buyer. By default the user of the update.</param>
public sealed record RefundStars(string ChargeId, long? UserId = null) : IBotResult;
