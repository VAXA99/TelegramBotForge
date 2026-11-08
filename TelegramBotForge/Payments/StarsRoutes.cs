using TelegramBotForge.Controllers;
using TelegramBotForge.Results;
using TelegramBotForge.Views;

namespace TelegramBotForge.Payments;

/// <summary>
/// Makes the method an action for the check before payment of a product, or of every product without a code.
/// The library has already checked the invoice, the buyer, the product and the price; the action adds its own checks
/// and returns <c>AcceptCheckout()</c> or <c>DeclineCheckout("reason")</c>. The order is bound to a <see cref="StarsOrder"/> parameter.
/// </summary>
/// <remarks>
/// Telegram cancels the payment if the bot does not answer within 10 seconds, so keep it fast.
/// Without such an action, orders are accepted once the built-in checks pass.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class PreCheckoutAttribute : RouteAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PreCheckoutAttribute"/> class.
    /// </summary>
    /// <param name="productCode">The product, or <c>null</c> for every product without its own action.</param>
    public PreCheckoutAttribute(string? productCode = null)
    {
        if (productCode is not null && !StarsProduct.IsValidCode(productCode))
            throw new ArgumentException($"\"{productCode}\" is not a valid Stars product code.", nameof(productCode));
        ProductCode = productCode;
    }

    /// <summary>
    /// Gets the product, or <c>null</c> for every product.
    /// </summary>
    public string? ProductCode { get; }

    public override int Priority => ProductCode is null ? 100 : 110;

    public override string Key => "pre-checkout " + (ProductCode ?? "*");

    public override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.StarsOrder is { } order && (ProductCode is null || order.Product.Code == ProductCode);
}

/// <summary>
/// Makes the method an action for a completed payment of a product, or of every product without a code.
/// The payment is bound to a <see cref="StarsPayment"/> parameter. Every payment reaches exactly one action, once:
/// duplicates are skipped.
/// </summary>
/// <remarks>
/// If the action fails, the payment is not marked as processed, and the error is logged with its <c>ChargeId</c>.
/// At startup, every product must have a <c>[Paid]</c> action.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class PaidAttribute : RouteAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PaidAttribute"/> class.
    /// </summary>
    /// <param name="productCode">The product, or <c>null</c> for every product without its own action.</param>
    public PaidAttribute(string? productCode = null)
    {
        if (productCode is not null && !StarsProduct.IsValidCode(productCode))
            throw new ArgumentException($"\"{productCode}\" is not a valid Stars product code.", nameof(productCode));
        ProductCode = productCode;
    }

    /// <summary>
    /// Gets the product, or <c>null</c> for every product.
    /// </summary>
    public string? ProductCode { get; }

    public override int Priority => ProductCode is null ? 100 : 110;

    public override string Key => "paid " + (ProductCode ?? "*");

    public override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.StarsPayment is { } payment && (ProductCode is null || payment.ProductCode == ProductCode);
}

/// <summary>
/// The built-in answer to <c>/paysupport</c>, which Telegram requires from bots that sell with Stars: shows <see cref="PaySupportView"/>.
/// Redefine the view to change the message, or write your own <c>[Command("paysupport")]</c> action to replace the command.
/// </summary>
internal sealed class PaySupportController : BotController
{
    private readonly PaySupportView _view;

    public PaySupportController(PaySupportView view) => _view = view;

    [Command("paysupport")]
    public IBotResult? Support() => ViewResults.From(_view);
}
