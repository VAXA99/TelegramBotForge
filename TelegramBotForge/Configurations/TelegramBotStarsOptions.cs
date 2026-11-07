namespace TelegramBotForge.Configurations;

/// <summary>
/// Settings of Telegram Stars payments, bound from the <c>Stars</c> subsection. Used when <c>AddStars</c> is called.
/// </summary>
/// <example>
/// <code>
/// "Stars": {
///   "PaySupportMessage": "Problems with a purchase? Write to @MyShopSupport. Telegram support can't help with purchases made in this bot.",
///   "PreCheckoutTimeoutSeconds": 8
/// }
/// </code>
/// </example>
public sealed class TelegramBotStarsOptions
{
    /// <summary>
    /// Gets or sets the answer to <c>/paysupport</c>, which Telegram requires from bots that sell with Stars:
    /// how users reach you about a purchase. Required when Stars are enabled, unless you write your own
    /// <c>[Command("paysupport")]</c> action. Plain text.
    /// </summary>
    public string? PaySupportMessage { get; set; }

    /// <summary>
    /// Gets or sets the text the user sees when an order is declined by the built-in checks
    /// (the price changed, the product was removed...) or when the check took too long.
    /// </summary>
    public string DeclineMessage { get; set; } = "Payment verification failed. Please try again.";

    /// <summary>
    /// Gets or sets how long, in seconds, the check before payment may take before the order is declined.
    /// Telegram cancels the payment after 10 seconds, so 1–9. The default is 8.
    /// </summary>
    public int PreCheckoutTimeoutSeconds { get; set; } = 8;
}
