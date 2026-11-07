using System.Collections;

namespace TelegramBotForge.Logging;

/// <summary>
/// The log scope opened while an update is processed. Every log line written by actions during that time,
/// including your own, carries the chat and user of the update.
/// </summary>
internal sealed class UpdateLogScope : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly long _chatId;
    private readonly long _userId;

    public UpdateLogScope(long chatId, long userId)
    {
        _chatId = chatId;
        _userId = userId;
    }

    public int Count => 2;

    public KeyValuePair<string, object?> this[int index] => index switch
    {
        0 => new("ChatId", _chatId),
        1 => new("UserId", _userId),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        yield return this[0];
        yield return this[1];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"ChatId={_chatId} UserId={_userId}";
}
