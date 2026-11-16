using TelegramBotForge.Media;
using TelegramBotForge.Views;

namespace TelegramBotForge.Results;

/// <summary>Actual output IDs. Persist this receipt and pass it to Edit to manage associated media.</summary>
public sealed class MessageDelivery
{
    public long ChatId { get; }
    public int PrimaryMessageId { get; }
    public IReadOnlyList<int> AssociatedMessageIds { get; }
    public bool Rich { get; }
    internal MediaFile[]? Media { get; init; }

    public MessageDelivery(long chatId, int primaryMessageId, IReadOnlyList<int> associatedMessageIds, bool rich)
    {
        ChatId = chatId;
        PrimaryMessageId = primaryMessageId;
        AssociatedMessageIds = Array.AsReadOnly(associatedMessageIds.ToArray());
        Rich = rich;
    }
}

/// <summary>Delivers a structured message. A previous receipt enables cleanup across multi-message screen updates.</summary>
public sealed record DeliverMessage(Message Content, bool Edit = false, int? MessageId = null, MessageDelivery? Previous = null) : IBotResult;
