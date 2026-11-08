using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using TelegramBotForge.Configurations;

namespace TelegramBotForge.Hosting;

/// <summary>
/// Wraps the Telegram client so that sent messages wait for their turn under Telegram's limits:
/// per chat first, then for the whole bot. Every other call goes straight through.
/// </summary>
internal sealed class FloodControlledBotClient : DelegatingBotClient, IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan SlowWait = TimeSpan.FromSeconds(1);

    private readonly ILogger _logger;
    private readonly TokenBucketRateLimiter _global;
    private readonly PartitionedRateLimiter<long> _chats;

    public FloodControlledBotClient(ITelegramBotClient inner, TelegramBotFloodControlOptions options, ILogger logger)
        : base(inner)
    {
        _logger = logger;

        _global = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = options.GlobalPerSecond,
            TokensPerPeriod = options.GlobalPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

        _chats = PartitionedRateLimiter.Create<long, long>(chatId => chatId > 0
            ? RateLimitPartition.GetTokenBucketLimiter(chatId, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = options.PrivateChatBurst,
                TokensPerPeriod = options.PrivateChatPerSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = int.MaxValue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            })
            : RateLimitPartition.GetSlidingWindowLimiter(chatId, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = options.GroupPerMinute,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = int.MaxValue,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }));
    }

    public override async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        if (!IsLimited(request, out var chatKey))
            return await Inner.SendRequest(request, cancellationToken);

        var started = Stopwatch.GetTimestamp();
        using (var chatLease = await _chats.AcquireAsync(chatKey, 1, cancellationToken))
        using (var globalLease = await _global.AcquireAsync(1, cancellationToken))
        {
            if (!chatLease.IsAcquired || !globalLease.IsAcquired)
                throw new InvalidOperationException("Flood control is shutting down.");
        }

        var waited = Stopwatch.GetElapsedTime(started);
        Telemetry.BotTelemetry.FloodControlWait.Record(waited.TotalSeconds);
        if (waited >= SlowWait)
        {
            _logger.LogDebug("{Method} to chat {ChatKey} waited {WaitedMs} ms for Telegram's limits",
                request.MethodName, chatKey, (int)waited.TotalMilliseconds);
        }

        return await Inner.SendRequest(request, cancellationToken);
    }

    public void Dispose()
    {
        _chats.Dispose();
        _global.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _chats.DisposeAsync();
        await _global.DisposeAsync();
    }

    /// <summary>
    /// Requests that put a new message into a chat: <c>send*</c> (but not chat actions or drafts), <c>copy*</c> and <c>forward*</c>.
    /// </summary>
    internal static bool IsLimited(IRequest request, out long chatKey)
    {
        chatKey = 0;
        if (request is not IChatTargetable { ChatId: { } chatId })
            return false;

        var method = request.MethodName;
        var sendsMessage =
            (method.StartsWith("send", StringComparison.Ordinal) &&
             method is not "sendChatAction" && !method.EndsWith("Draft", StringComparison.Ordinal)) ||
            method.StartsWith("copyMessage", StringComparison.Ordinal) ||
            method.StartsWith("forwardMessage", StringComparison.Ordinal);

        if (!sendsMessage)
            return false;

        // Channels by @username are limited like groups: a negative key.
        chatKey = chatId.Identifier ?? -Math.Abs((long)StringComparer.OrdinalIgnoreCase.GetHashCode(chatId.Username ?? string.Empty)) - 1;
        return true;
    }
}
