using System.Globalization;

namespace TelegramBotForge.Payments;

/// <summary>
/// Something users can buy with Telegram Stars.
/// </summary>
/// <param name="Code">The id of the product in your code and in <c>[Paid("code")]</c>: 1–32 letters, digits, <c>_</c>, <c>-</c> or <c>.</c>.</param>
/// <param name="Title">The name on the invoice, 1–32 characters.</param>
/// <param name="Description">The description on the invoice, 1–255 characters.</param>
/// <param name="Price">The price in Stars, at least 1.</param>
public sealed record StarsProduct(string Code, string Title, string Description, int Price)
{
    internal const int MaxCodeLength = 32;

    /// <summary>
    /// Checks the product against Telegram's limits.
    /// </summary>
    /// <exception cref="ArgumentException">A value is outside Telegram's limits.</exception>
    public void Validate()
    {
        if (!IsValidCode(Code))
            throw new ArgumentException($"Stars product code \"{Code}\" is not valid: use 1 to {MaxCodeLength} letters, digits, '_', '-' or '.'.");
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 32)
            throw new ArgumentException($"Stars product \"{Code}\" needs a title of 1 to 32 characters.");
        if (string.IsNullOrWhiteSpace(Description) || Description.Length > 255)
            throw new ArgumentException($"Stars product \"{Code}\" needs a description of 1 to 255 characters.");
        if (Price < 1)
            throw new ArgumentException($"Stars product \"{Code}\" needs a price of at least 1 Star.");
    }

    internal static bool IsValidCode(string? code) =>
        !string.IsNullOrEmpty(code) &&
        code.Length <= MaxCodeLength &&
        code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}

/// <summary>
/// Where the bot finds its products. The default is the list given to <c>AddStars</c>; register your own with
/// <c>UseStarsProducts&lt;T&gt;()</c>, for example to read products and prices from a database.
/// </summary>
public interface IStarsProducts
{
    /// <summary>
    /// Finds a product by its code, or returns <c>null</c>.
    /// </summary>
    ValueTask<StarsProduct?> FindAsync(string code, CancellationToken ct);
}

/// <summary>
/// The products given to <c>AddStars</c>, kept in memory.
/// </summary>
/// <example>
/// <code>
/// .AddStars(stars => stars
///     .Product("premium_month", "Premium", "30 days of Premium", price: 100)
///     .Product("boost", "Boost", "Your profile first for a day", price: 25))
/// </code>
/// </example>
public sealed class StarsCatalog : IStarsProducts
{
    private readonly Dictionary<string, StarsProduct> _products = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets all products.
    /// </summary>
    public IReadOnlyCollection<StarsProduct> Products => _products.Values;

    /// <summary>
    /// Adds a product.
    /// </summary>
    public StarsCatalog Product(string code, string title, string description, int price) =>
        Product(new StarsProduct(code, title, description, price));

    /// <summary>
    /// Adds a product.
    /// </summary>
    /// <exception cref="ArgumentException">The product is not valid, or its code is used twice.</exception>
    public StarsCatalog Product(StarsProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        product.Validate();
        if (!_products.TryAdd(product.Code, product))
            throw new ArgumentException($"Stars product \"{product.Code}\" is added twice.", nameof(product));
        return this;
    }

    /// <inheritdoc/>
    public ValueTask<StarsProduct?> FindAsync(string code, CancellationToken ct) =>
        ValueTask.FromResult(_products.GetValueOrDefault(code));
}

/// <summary>
/// An order waiting for payment, checked by the library: valid invoice, the same buyer, the product still exists,
/// and the price has not changed. A <c>[PreCheckout]</c> action gets it as a parameter.
/// </summary>
/// <param name="Product">The product.</param>
/// <param name="UserId">The buyer.</param>
/// <param name="OrderId">The id of this order, created with the invoice.</param>
public sealed record StarsOrder(StarsProduct Product, long UserId, string OrderId);

/// <summary>
/// A completed payment. A <c>[Paid]</c> action gets it as a parameter. The Stars are already taken:
/// give the user what they bought, or refund with <see cref="ChargeId"/>.
/// </summary>
/// <param name="ProductCode">The code of the product.</param>
/// <param name="Product">The product, or <c>null</c> if it was removed after the invoice was sent.</param>
/// <param name="UserId">The buyer.</param>
/// <param name="Amount">The amount paid, in Stars.</param>
/// <param name="ChargeId">Telegram's id of the payment: needed for refunds and unique per payment.</param>
/// <param name="OrderId">The id of the order, created with the invoice.</param>
public sealed record StarsPayment(
    string ProductCode,
    StarsProduct? Product,
    long UserId,
    long Amount,
    string ChargeId,
    string OrderId);

/// <summary>
/// The invoice payload: <c>tbf:stars:{product}:{userId}:{orderId}</c>. It ties every payment to a product, a buyer and an order.
/// </summary>
internal readonly record struct StarsPayload(string ProductCode, long UserId, string OrderId)
{
    public const string Prefix = "tbf:stars:";
    public const string Currency = "XTR";

    public static StarsPayload New(string productCode, long userId) => new(productCode, userId, Guid.NewGuid().ToString("N"));

    public override string ToString() =>
        $"{Prefix}{ProductCode}:{UserId.ToString(CultureInfo.InvariantCulture)}:{OrderId}";

    public static bool IsStars(string? payload) =>
        payload is not null && payload.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool TryParse(string? payload, out StarsPayload result)
    {
        result = default;
        if (!IsStars(payload))
            return false;

        var parts = payload![Prefix.Length..].Split(':');
        if (parts.Length != 3 ||
            !StarsProduct.IsValidCode(parts[0]) ||
            !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var userId) ||
            parts[2].Length != 32 ||
            !parts[2].All(char.IsAsciiHexDigitLower))
        {
            return false;
        }

        result = new StarsPayload(parts[0], userId, parts[2]);
        return true;
    }
}
