using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

namespace TelegramBotForge.Results;

internal sealed class MessageDeletionScheduler(ITelegramBotClient bot, TimeProvider time, ILogger<MessageDeletionScheduler> logger) : BackgroundService
{
    private readonly Channel<(MessageDelivery Receipt, DateTimeOffset Due)> _pending = Channel.CreateUnbounded<(MessageDelivery, DateTimeOffset)>(
        new UnboundedChannelOptions { SingleReader = true });

    public void Schedule(MessageDelivery receipt, TimeSpan delay) =>
        _pending.Writer.TryWrite((receipt, time.GetUtcNow() + delay));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = new PriorityQueue<MessageDelivery, DateTimeOffset>();
        while (!stoppingToken.IsCancellationRequested)
        {
            while (_pending.Reader.TryRead(out var pending))
                queue.Enqueue(pending.Receipt, pending.Due);

            if (queue.TryPeek(out var receipt, out var due) && due <= time.GetUtcNow())
            {
                queue.Dequeue();
                foreach (var messageId in receipt.AssociatedMessageIds.Prepend(receipt.PrimaryMessageId).Distinct())
                {
                    try { await DeleteAsync(bot, receipt.ChatId, messageId, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception exception) { logger.LogWarning(exception, "Could not delete expired message {MessageId} in chat {ChatId}", messageId, receipt.ChatId); }
                }
                continue;
            }

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var incoming = _pending.Reader.WaitToReadAsync(wake.Token).AsTask();
            var delay = queue.Count == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromTicks(Math.Max(0, (due - time.GetUtcNow()).Ticks));
            var timer = Task.Delay(delay, time, wake.Token);
            try { await await Task.WhenAny(incoming, timer); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            finally { await wake.CancelAsync(); }
        }
    }

    internal static async Task DeleteAsync(ITelegramBotClient bot, long chatId, int messageId, CancellationToken ct)
    {
        try { await bot.DeleteMessage(chatId, messageId, cancellationToken: ct); }
        catch (ApiRequestException exception) when (exception.ErrorCode == 400 &&
            exception.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase)) { }
    }
}
