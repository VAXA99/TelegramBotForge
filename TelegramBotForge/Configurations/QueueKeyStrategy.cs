namespace TelegramBotForge.Configurations;

/// <summary>
/// Defines how incoming updates are grouped into ordered queues.
/// Updates in the same queue are processed in order; different queues are processed in parallel.
/// Every strategy keeps all updates of one conversation (a user in a chat) in one queue,
/// so the conversation state is never changed by two updates at once. Set with <see cref="TelegramBotBuilder.QueueBy"/>.
/// </summary>
public enum QueueKeyStrategy
{
    /// <summary>
    /// One queue per chat. Keeps the order of everything that happens in a chat. The default.
    /// </summary>
    Chat,

    /// <summary>
    /// One queue per user, across all chats.
    /// </summary>
    User,

    /// <summary>
    /// One queue per user in each chat. Useful in groups, where users should not wait for each other.
    /// </summary>
    ChatAndUser
}
