using TelegramBotForge.Views;

namespace TelegramBotForge.Broadcasting;

/// <summary>
/// Sends a message to many chats: announcements, newsletters, reminders. Inject it anywhere (a background job,
/// an admin action, an ASP.NET endpoint).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>It sends at <see cref="BroadcastOptions.MessagesPerSecond"/>, under Telegram's limit, and leaves room for the bot's answers to users.</item>
/// <item>Users who blocked the bot, deleted their account or left the group are counted in <see cref="BroadcastReport.Unreachable"/>
/// and reported to <see cref="BroadcastOptions.OnUnreachable"/>, so you can stop sending to them.</item>
/// <item>It never throws for one chat: errors are collected in the report. Cancelling stops it and returns the report too.</item>
/// <item>To resume an interrupted or partly failed broadcast, send again to <see cref="BroadcastReport.Remaining"/>.
/// To survive a restart, record each delivery in <see cref="BroadcastOptions.OnSent"/> and leave those chats out next time.</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// var report = await broadcaster.SendAsync(
///     await users.ActiveIdsAsync(),
///     news.Render(post),
///     new BroadcastOptions { OnUnreachable = (chatId, ct) => users.MarkInactiveAsync(chatId, ct) },
///     ct);
/// logger.LogInformation("Sent {Sent}, unreachable {Unreachable}, failed {Failed}", report.Sent, report.Unreachable.Count, report.Failed.Count);
/// </code>
/// </example>
public interface IBotBroadcaster
{
    /// <summary>
    /// Sends the same message to every chat.
    /// </summary>
    /// <param name="chatIds">The chats; repeated ids are sent to once.</param>
    /// <param name="message">The message, for example a rendered view.</param>
    /// <param name="options">Speed and callbacks; the defaults fit most bots.</param>
    /// <param name="ct">Stops the broadcast; the report says what was sent.</param>
    Task<BroadcastReport> SendAsync(IEnumerable<long> chatIds, BotMessage message, BroadcastOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Sends a message made for each chat, for example in the user's language. Return <c>null</c> to skip a chat.
    /// </summary>
    /// <param name="chatIds">The chats; repeated ids are sent to once.</param>
    /// <param name="render">Makes the message of one chat.</param>
    /// <param name="options">Speed and callbacks; the defaults fit most bots.</param>
    /// <param name="ct">Stops the broadcast; the report says what was sent.</param>
    Task<BroadcastReport> SendAsync(
        IEnumerable<long> chatIds,
        Func<long, CancellationToken, ValueTask<BotMessage?>> render,
        BroadcastOptions? options = null,
        CancellationToken ct = default);
}

/// <summary>
/// How a broadcast runs.
/// </summary>
public sealed class BroadcastOptions
{
    /// <summary>
    /// Gets or sets how many messages are sent per second. Telegram allows about 30 per second for the whole bot;
    /// the default of 25 leaves room for the bot's answers to users.
    /// </summary>
    public int MessagesPerSecond { get; set; } = 25;

    /// <summary>
    /// Gets or sets a function called after each delivered message, with the chat and the new message id,
    /// for example to record the delivery so a restarted broadcast can skip it.
    /// </summary>
    public Func<long, int, CancellationToken, ValueTask>? OnSent { get; set; }

    /// <summary>
    /// Gets or sets a function called for each chat that can no longer get messages: the user blocked the bot,
    /// deleted the account, or the bot was removed from the group. Typically you mark the user inactive.
    /// </summary>
    public Func<long, CancellationToken, ValueTask>? OnUnreachable { get; set; }

    /// <summary>
    /// Gets or sets where progress is reported, about once a second and at the end.
    /// </summary>
    public IProgress<BroadcastProgress>? Progress { get; set; }
}

/// <summary>
/// How far a broadcast is.
/// </summary>
/// <param name="Total">The number of chats.</param>
/// <param name="Sent">Messages delivered so far.</param>
/// <param name="Unreachable">Chats that can no longer get messages.</param>
/// <param name="Failed">Chats where sending failed for another reason.</param>
/// <param name="Skipped">Chats the render function returned <c>null</c> for.</param>
public sealed record BroadcastProgress(int Total, int Sent, int Unreachable, int Failed, int Skipped)
{
    /// <summary>
    /// Gets the number of chats done, in any way.
    /// </summary>
    public int Done => Sent + Unreachable + Failed + Skipped;
}

/// <summary>
/// The result of a broadcast.
/// </summary>
public sealed class BroadcastReport
{
    internal BroadcastReport(
        int total, int sent, int skipped, IReadOnlyList<long> unreachable, IReadOnlyDictionary<long, string> failed,
        IReadOnlyList<long> notStarted, bool cancelled, TimeSpan duration)
    {
        Total = total;
        Sent = sent;
        Skipped = skipped;
        Unreachable = unreachable;
        Failed = failed;
        Cancelled = cancelled;
        Duration = duration;
        Remaining = [.. failed.Keys, .. notStarted];
    }

    /// <summary>
    /// Gets the number of chats.
    /// </summary>
    public int Total { get; }

    /// <summary>
    /// Gets the number of delivered messages.
    /// </summary>
    public int Sent { get; }

    /// <summary>
    /// Gets the number of chats the render function returned <c>null</c> for.
    /// </summary>
    public int Skipped { get; }

    /// <summary>
    /// Gets the chats that can no longer get messages (blocked the bot, deleted account, bot removed from the group).
    /// </summary>
    public IReadOnlyList<long> Unreachable { get; }

    /// <summary>
    /// Gets the chats where sending failed for another reason, with the error.
    /// </summary>
    public IReadOnlyDictionary<long, string> Failed { get; }

    /// <summary>
    /// Gets the chats worth sending to again: the failed ones and, after a cancellation, the ones not reached yet.
    /// </summary>
    public IReadOnlyList<long> Remaining { get; }

    /// <summary>
    /// Gets a value indicating whether the broadcast was cancelled before it reached every chat.
    /// </summary>
    public bool Cancelled { get; }

    /// <summary>
    /// Gets how long the broadcast took.
    /// </summary>
    public TimeSpan Duration { get; }
}
