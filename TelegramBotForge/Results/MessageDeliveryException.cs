namespace TelegramBotForge.Results;

/// <summary>A confirmed delivery followed by callback, finalization or cleanup errors. Do not resend this delivery.</summary>
public sealed class MessageDeliveryException : AggregateException
{
    public MessageDeliveryException(MessageDelivery delivery, IEnumerable<Exception> errors)
        : base("The message was delivered, but post-delivery processing failed. Do not resend it.", errors)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        Delivery = delivery;
    }

    public MessageDelivery Delivery { get; }
}
