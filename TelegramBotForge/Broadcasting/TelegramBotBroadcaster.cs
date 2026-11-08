using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using TelegramBotForge.Controllers;
using TelegramBotForge.Results;
using TelegramBotForge.Telemetry;
using TelegramBotForge.Views;

namespace TelegramBotForge.Broadcasting;

internal sealed class TelegramBotBroadcaster : IBotBroadcaster
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    private readonly ITelegramBotClient _bot;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger _logger;

    public TelegramBotBroadcaster(ITelegramBotClient bot, IServiceScopeFactory scopes, ILoggerFactory loggers)
    {
        _bot = bot;
        _scopes = scopes;
        _logger = loggers.CreateLogger("TelegramBotForge.Broadcast");
    }

    public Task<BroadcastReport> SendAsync(IEnumerable<long> chatIds, BotMessage message, BroadcastOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return SendAsync(chatIds, (_, _) => ValueTask.FromResult<BotMessage?>(message), options, ct);
    }

    public async Task<BroadcastReport> SendAsync(
        IEnumerable<long> chatIds,
        Func<long, CancellationToken, ValueTask<BotMessage?>> render,
        BroadcastOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chatIds);
        ArgumentNullException.ThrowIfNull(render);
        options ??= new BroadcastOptions();
        if (options.MessagesPerSecond < 1)
            throw new ArgumentOutOfRangeException(nameof(options), options.MessagesPerSecond, "MessagesPerSecond must be at least 1.");

        var chats = chatIds.Distinct().ToList();
        var started = Stopwatch.GetTimestamp();
        var unreachable = new ConcurrentBag<long>();
        var failed = new ConcurrentDictionary<long, string>();
        var notStarted = new ConcurrentBag<long>();
        int sent = 0, skipped = 0;

        _logger.LogInformation("Broadcast to {Total} chats started, {PerSecond} messages/s", chats.Count, options.MessagesPerSecond);

        await using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = options.MessagesPerSecond,
            TokensPerPeriod = options.MessagesPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = int.MaxValue,
            AutoReplenishment = true
        });

        BroadcastProgress Progress() => new(chats.Count, sent, unreachable.Count, failed.Count, skipped);
        var lastProgress = Stopwatch.GetTimestamp();
        var progressGate = new object();

        void ReportProgress(bool force = false)
        {
            if (options.Progress is null)
                return;
            lock (progressGate)
            {
                if (!force && Stopwatch.GetElapsedTime(lastProgress) < ProgressInterval)
                    return;
                lastProgress = Stopwatch.GetTimestamp();
            }

            options.Progress.Report(Progress());
        }

        await using var scope = _scopes.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<IBotResultExecutor>();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(options.MessagesPerSecond, 1, 30) };

        // Not cancelled by ct itself, so every chat is accounted for: the ones not started go to Remaining.
        await Parallel.ForEachAsync(chats, parallel, async (chatId, _) =>
        {
            if (ct.IsCancellationRequested)
            {
                notStarted.Add(chatId);
                return;
            }

            try
            {
                var message = await render(chatId, ct);
                if (message is null)
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                // Token bucket permits are used up, not given back, so the lease is released at once.
                (await limiter.AcquireAsync(1, ct)).Dispose();

                var messageId = await SendAsync(executor, scope.ServiceProvider, chatId, message, ct);
                Interlocked.Increment(ref sent);
                BotTelemetry.BroadcastMessages.Add(1, new KeyValuePair<string, object?>("outcome", "sent"));
                if (options.OnSent is not null)
                    await options.OnSent(chatId, messageId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                notStarted.Add(chatId);
            }
            catch (ApiRequestException ex) when (IsUnreachable(ex))
            {
                unreachable.Add(chatId);
                BotTelemetry.BroadcastMessages.Add(1, new KeyValuePair<string, object?>("outcome", "unreachable"));
                if (options.OnUnreachable is not null)
                    await SafeAsync(() => options.OnUnreachable(chatId, CancellationToken.None), chatId);
            }
            catch (Exception ex)
            {
                failed[chatId] = ex.Message;
                BotTelemetry.BroadcastMessages.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
                _logger.LogWarning(ex, "Broadcast to chat {ChatId} failed", chatId);
            }
            finally
            {
                ReportProgress();
            }
        });

        ReportProgress(force: true);
        var report = new BroadcastReport(chats.Count, sent, skipped, unreachable.ToList(), failed, notStarted.ToList(),
            ct.IsCancellationRequested, Stopwatch.GetElapsedTime(started));

        _logger.LogInformation(
            "Broadcast {Status}: {Sent} sent, {Unreachable} unreachable, {Failed} failed, {Skipped} skipped of {Total} in {Seconds:0.0}s",
            report.Cancelled ? "cancelled" : "finished", report.Sent, report.Unreachable.Count, report.Failed.Count, report.Skipped,
            report.Total, report.Duration.TotalSeconds);
        return report;
    }

    /// <summary>
    /// Sends with the same code as the answers of actions, so text, media, files and buttons work the same way.
    /// </summary>
    private async Task<int> SendAsync(IBotResultExecutor executor, IServiceProvider services, long chatId, BotMessage message, CancellationToken ct)
    {
        var sentId = 0;
        var update = new Update { Message = new Message { Chat = new Chat { Id = chatId, Type = chatId > 0 ? Telegram.Bot.Types.Enums.ChatType.Private : Telegram.Bot.Types.Enums.ChatType.Supergroup } } };
        var bot = new CapturingBotClient(_bot, id => sentId = id);
        var context = new BotContext(update, bot, services, ct);
        await executor.ExecuteAsync(context, ViewResults.From(message), ct);
        return sentId;
    }

    /// <summary>
    /// 403: blocked by the user, kicked from the group, deactivated user. 400: chat not found, user deactivated.
    /// </summary>
    internal static bool IsUnreachable(ApiRequestException ex) =>
        ex.ErrorCode == 403 ||
        ex.ErrorCode == 400 && (ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("deactivated", StringComparison.OrdinalIgnoreCase) ||
                                ex.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase));

    private async Task SafeAsync(Func<ValueTask> callback, long chatId)
    {
        try
        {
            await callback();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OnUnreachable failed for chat {ChatId}", chatId);
        }
    }

    /// <summary>
    /// Remembers the id of the message the executor sent.
    /// </summary>
    private sealed class CapturingBotClient(ITelegramBotClient inner, Action<int> sent) : Hosting.DelegatingBotClient(inner)
    {
        public override async Task<TResponse> SendRequest<TResponse>(Telegram.Bot.Requests.Abstractions.IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var response = await Inner.SendRequest(request, cancellationToken);
            if (response is Message message)
                sent(message.Id);
            return response;
        }
    }
}
