using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types.Payments;
using TelegramBotForge.Logging;
using TelegramBotForge.Middleware;
using TelegramBotForge.Results;
using TelegramBotForge.Views;

namespace TelegramBotForge.Payments;

/// <summary>
/// Built-in middleware, added by <c>AddStars</c>, right before the conversation state.
/// Before payment, it checks the order and makes sure the check is always answered.
/// After payment, it skips duplicates and makes sure the payment reaches a <c>[Paid]</c> action.
/// </summary>
public sealed class StarsMiddleware : IBotMiddleware
{
    private readonly IStarsProducts _products;
    private readonly IProcessedPayments _processed;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StarsMiddleware"/> class.
    /// </summary>
    public StarsMiddleware(IStarsProducts products, IProcessedPayments processed, ILoggerFactory loggerFactory)
    {
        _products = products;
        _processed = processed;
        _logger = loggerFactory.CreateLogger("TelegramBotForge.Payments");
    }

    /// <inheritdoc/>
    public Task<IBotResult?> InvokeAsync(BotContext context, BotNext next, CancellationToken ct)
    {
        if (context.Update.PreCheckoutQuery is { } query && StarsPayload.IsStars(query.InvoicePayload))
            return PreCheckoutAsync(context, query, next, ct);

        if (context.Message?.SuccessfulPayment is { } payment && StarsPayload.IsStars(payment.InvoicePayload))
            return PaidAsync(context, payment, next, ct);

        return next(context, ct);
    }

    private async Task<IBotResult?> PreCheckoutAsync(BotContext context, PreCheckoutQuery query, BotNext next, CancellationToken ct)
    {
        var (order, problem) = await VerifyAsync(query, ct);
        if (order is null)
        {
            _logger.LogWarning("Declined a Stars order: {Problem}. Payload={Payload}, Amount={Amount} {Currency}",
                problem, query.InvoicePayload, query.TotalAmount, query.Currency);
            UpdateTrace.Mark("pre-checkout declined");
            return new AnswerPreCheckout(false, DeclineText(context.Services));
        }

        context.StarsOrder = order;

        IBotResult? result;
        try
        {
            result = await next(context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without an answer the payment would hang until Telegram cancels it.
            _logger.LogError(ex, "The check before payment of \"{Product}\" failed, so the order was declined", order.Product.Code);
            UpdateTrace.MarkFailed();
            return new AnswerPreCheckout(false, DeclineText(context.Services));
        }

        // The check must always be answered; without an answer from the action, the order is accepted.
        return result switch
        {
            null => AnswerPreCheckout.Accept,
            _ when Answers(result) => result,
            _ => AnswerPreCheckout.Accept.Then(result)
        };
    }

    private async Task<IBotResult?> PaidAsync(BotContext context, SuccessfulPayment payment, BotNext next, CancellationToken ct)
    {
        var parsed = StarsPayload.TryParse(payment.InvoicePayload, out var payload);
        var code = parsed ? payload.ProductCode : string.Empty;
        var product = parsed ? await _products.FindAsync(code, ct) : null;

        if (!await _processed.TryAddAsync(payment.TelegramPaymentChargeId, ct))
        {
            _logger.LogWarning("Skipped payment {ChargeId}: it was already processed", payment.TelegramPaymentChargeId);
            UpdateTrace.Mark("duplicate payment");
            return null;
        }

        context.StarsPayment = new StarsPayment(
            code,
            product,
            context.UserId ?? payload.UserId,
            payment.TotalAmount,
            payment.TelegramPaymentChargeId,
            parsed ? payload.OrderId : string.Empty);

        try
        {
            var result = await next(context, ct);

            if (context.HandledBy is null)
            {
                _logger.LogError(
                    "Payment {ChargeId} for {Product} ({Amount} Stars from user {UserId}) reached no [Paid] action: " +
                    "the user paid and got nothing. Handle it or refund it",
                    payment.TelegramPaymentChargeId, code, payment.TotalAmount, context.UserId);
            }

            return result;
        }
        catch
        {
            // Not processed: allow it to be processed again.
            await _processed.RemoveAsync(payment.TelegramPaymentChargeId, CancellationToken.None);
            _logger.LogError("Payment {ChargeId} for {Product} ({Amount} Stars) failed in its action: give the user what they bought, or refund it",
                payment.TelegramPaymentChargeId, code, payment.TotalAmount);
            throw;
        }
    }

    private async Task<(StarsOrder? Order, string? Problem)> VerifyAsync(PreCheckoutQuery query, CancellationToken ct)
    {
        if (!StarsPayload.TryParse(query.InvoicePayload, out var payload))
            return (null, "the invoice payload is not valid");
        if (!string.Equals(query.Currency, StarsPayload.Currency, StringComparison.Ordinal))
            return (null, $"the currency is {query.Currency}, not Stars");
        if (payload.UserId != query.From.Id)
            return (null, $"the invoice was made for user {payload.UserId}, but user {query.From.Id} pays");

        var product = await _products.FindAsync(payload.ProductCode, ct);
        if (product is null)
            return (null, $"product \"{payload.ProductCode}\" does not exist anymore");
        if (product.Price != query.TotalAmount)
            return (null, $"the price of \"{product.Code}\" changed from {query.TotalAmount} to {product.Price} Stars");

        return (new StarsOrder(product, query.From.Id, payload.OrderId), null);
    }

    /// <summary>
    /// The text of <see cref="PaymentDeclinedView"/>. Telegram requires a text when an order is declined.
    /// </summary>
    internal static string DeclineText(IServiceProvider services)
    {
        const string fallback = "Payment verification failed. Please try again.";
        try
        {
            return services.GetRequiredService<PaymentDeclinedView>().RenderPlainText() is { Length: > 0 } text ? text : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static bool Answers(IBotResult result) =>
        result is AnswerPreCheckout || result is MultiResult multi && multi.Results.Any(Answers);
}
