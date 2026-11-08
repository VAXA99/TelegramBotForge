using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Payments;
using TelegramBotForge.Configurations;
using TelegramBotForge.Logging;
using TelegramBotForge.Payments;

namespace TelegramBotForge.Hosting;

/// <summary>
/// The single entry point of the bot: long polling and the webhook both put updates here.
/// </summary>
public interface IBotUpdateQueue
{
    /// <summary>
    /// Queues an update for processing. When its queue is full, it waits for free space,
    /// or drops the oldest update of that queue if <see cref="TelegramBotConfiguration.DropOnOverflow"/> is on.
    /// </summary>
    ValueTask EnqueueAsync(Update update, CancellationToken ct);
}

/// <summary>
/// An update waiting in a queue.
/// </summary>
/// <param name="Update">The update.</param>
/// <param name="CallbackAnswered">The button press was already answered when it arrived (<see cref="CallbackAckMode.Immediate"/>).</param>
internal readonly record struct QueuedUpdate(Update Update, bool CallbackAnswered);

/// <summary>
/// One ordered queue per key (a chat, a user or a chat/user pair, see <see cref="TelegramBotBuilder.QueueBy"/>).
/// Updates of one queue are processed in order; different queues run in parallel, at most
/// <see cref="TelegramBotConfiguration.MaxGlobalConcurrency"/> updates at a time. Idle queues are removed,
/// and on shutdown the updates already accepted are finished before the bot stops.
/// </summary>
internal sealed class UpdateQueues : BackgroundService, IBotUpdateQueue
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(30);
    private const int MaxEnqueueAttempts = 3;

    private readonly ConcurrentDictionary<long, Partition> _partitions = new();
    private readonly UpdateDispatcher _dispatcher;
    private readonly IServiceScopeFactory _scopes;
    private readonly ITelegramBotClient _bot;
    private readonly TelegramBotConfiguration _config;
    private readonly CallbackAckMode _ackMode;
    private readonly QueueKeyStrategy _keyStrategy;
    private readonly int _parallelPerQueue;
    private readonly ILogger<UpdateQueues> _logger;
    private readonly TimeProvider _time;
    private readonly IConversationLock? _conversationLock;
    private readonly bool _collectAlbums;
    private readonly SemaphoreSlim _concurrency;
    private readonly CancellationTokenSource _processing = new();
    private volatile bool _accepting = true;

    public UpdateQueues(
        UpdateDispatcher dispatcher,
        IServiceScopeFactory scopes,
        ITelegramBotClient bot,
        IOptions<TelegramBotConfiguration> options,
        TelegramBotBuilder builder,
        ILogger<UpdateQueues> logger,
        TimeProvider? timeProvider = null,
        IConversationLock? conversationLock = null,
        Routing.ActionRegistry? registry = null)
    {
        // Albums are collected only for [Album] actions, and only when one consumer reads each queue in order.
        _collectAlbums = registry?.HasAlbumActions == true && builder.ParallelUpdatesPerQueue == 1;
        _conversationLock = conversationLock;
        _dispatcher = dispatcher;
        _scopes = scopes;
        _bot = bot;
        _config = options.Value;
        _ackMode = builder.CallbackAckMode;
        _keyStrategy = builder.QueueKeyStrategy;
        _parallelPerQueue = builder.ParallelUpdatesPerQueue;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _concurrency = new SemaphoreSlim(_config.MaxGlobalConcurrency, _config.MaxGlobalConcurrency);
        Telemetry.BotTelemetry.ObserveQueueLength(() => _partitions.Values.Sum(partition => (long)partition.Count));
    }

    public async ValueTask EnqueueAsync(Update update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (!_accepting)
        {
            _logger.LogDebug("Ignored update {UpdateId}: the bot is stopping", update.Id);
            return;
        }

        // Telegram cancels a payment whose check is not answered within 10 seconds, so checks never wait in a queue.
        if (update.PreCheckoutQuery is { } checkout)
        {
            _ = Task.Run(() => ProcessPreCheckoutAsync(update, checkout));
            return;
        }

        // Answered before queueing, so the button stops spinning even when its queue is busy.
        var callbackAnswered = false;
        if (update.CallbackQuery is { } query && _ackMode == CallbackAckMode.Immediate)
            callbackAnswered = await TryAnswerButtonAsync(query, ct);

        var item = new QueuedUpdate(update, callbackAnswered);
        var key = GetKey(update);

        for (var attempt = 0; attempt < MaxEnqueueAttempts && _accepting; attempt++)
        {
            var partition = GetOrCreatePartition(key);
            partition.Touch(_time.GetUtcNow());

            if (await partition.TryWriteAsync(item, _config.DropOnOverflow, ct))
                return;

            // Cleanup closed this queue at the same moment: forget it and write into a new one.
            _partitions.TryRemove(new KeyValuePair<long, Partition>(key, partition));
        }

        _logger.LogWarning("Dropped update {UpdateId}: its queue was closed. Key={Key}", update.Id, key);
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Queues: one per {KeyStrategy}, {Capacity} updates each ({OverflowMode} when full), " +
            "{MaxGlobalConcurrency} updates in parallel overall, {PerQueue} per queue",
            _keyStrategy,
            _config.PerChatQueueCapacity,
            _config.DropOnOverflow ? "drop oldest" : "wait",
            _config.MaxGlobalConcurrency,
            _parallelPerQueue);

        if (_parallelPerQueue > 1)
        {
            _logger.LogWarning(
                "ParallelPerQueue is {PerQueue}: updates of one conversation can run out of order " +
                "and overwrite each other's conversation state", _parallelPerQueue);
        }

        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Removes idle queues every 30 seconds.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CleanupInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                RemoveIdlePartitions();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>
    /// Stops accepting updates and finishes the ones already queued. If the host's shutdown timeout runs out first,
    /// the remaining work is cancelled.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _accepting = false;

        var partitions = _partitions.Values.ToArray();
        foreach (var partition in partitions)
            partition.Complete();

        var pending = partitions.Sum(partition => partition.Count);
        if (pending > 0)
            _logger.LogInformation("Stopping: finishing {PendingCount} queued updates", pending);

        await using (cancellationToken.Register(() => _processing.Cancel()))
        {
            try
            {
                await Task.WhenAll(partitions.SelectMany(partition => partition.Consumers));
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Stopped before all queued updates were processed");
            }
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _processing.Dispose();
        _concurrency.Dispose();
        base.Dispose();
    }

    private Partition GetOrCreatePartition(long key)
    {
        if (_partitions.TryGetValue(key, out var existing))
            return existing;

        var created = new Partition(key, CreateChannel(key), _time.GetUtcNow());
        var partition = _partitions.GetOrAdd(key, created);

        // Only the queue that won the race gets consumers, so no consumer reads from a queue nobody writes to.
        if (ReferenceEquals(partition, created))
        {
            partition.Consumers = Enumerable.Range(0, _parallelPerQueue)
                .Select(_ => Task.Run(() => ConsumeAsync(partition)))
                .ToArray();
            _logger.LogDebug("Queue created. Key={Key}", key);
        }

        return partition;
    }

    private Channel<QueuedUpdate> CreateChannel(long key)
    {
        var options = new BoundedChannelOptions(_config.PerChatQueueCapacity)
        {
            SingleReader = _parallelPerQueue == 1,
            SingleWriter = false,
            FullMode = _config.DropOnOverflow ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait
        };

        return Channel.CreateBounded<QueuedUpdate>(options, dropped =>
            _logger.LogWarning("Queue is full: dropped update {UpdateId}. Key={Key}", dropped.Update.Id, key));
    }

    /// <summary>
    /// Reads one queue in order. Each update waits for a free slot of the global limit.
    /// </summary>
    private async Task ConsumeAsync(Partition partition)
    {
        var ct = _processing.Token;
        try
        {
            // Not cancelled by shutdown: the queue is completed instead, so updates already accepted are finished.
            QueuedUpdate? carried = null;
            while (carried is not null || await partition.Reader.WaitToReadAsync(CancellationToken.None))
            {
                QueuedUpdate item;
                if (carried is { } next)
                {
                    item = next;
                    carried = null;
                }
                else if (!partition.Reader.TryRead(out item))
                {
                    continue;
                }

                partition.Touch(_time.GetUtcNow());

                // The next items of an album are read now; an update that is not part of it is processed right after.
                IReadOnlyList<Message>? album = null;
                if (_collectAlbums && item.Update.Message is { MediaGroupId: { } groupId } first)
                    (album, carried) = await CollectAlbumAsync(partition, first, groupId);

                await _concurrency.WaitAsync(ct);
                try
                {
                    if (_conversationLock is null)
                    {
                        await _dispatcher.DispatchAsync(item.Update, item.CallbackAnswered, ct, album);
                    }
                    else
                    {
                        await using (await _conversationLock.AcquireAsync(LockKey(partition.Key), ct))
                            await _dispatcher.DispatchAsync(item.Update, item.CallbackAnswered, ct, album);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // The dispatcher handles its own errors; this only keeps the queue alive if something slips through.
                    _logger.LogError(ex, "Update {UpdateId} failed", item.Update.Id);
                }
                finally
                {
                    _concurrency.Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown timeout reached.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A queue consumer stopped unexpectedly. Key={Key}", partition.Key);
        }
    }

    /// <summary>
    /// Reads the next items of an album from the queue, until none arrives for <c>AlbumWaitMilliseconds</c>.
    /// Returns the first update that is not part of the album too, so it is processed next and the order is kept.
    /// </summary>
    private async Task<(IReadOnlyList<Message> Album, QueuedUpdate? Next)> CollectAlbumAsync(Partition partition, Message first, string groupId)
    {
        var items = new List<Message> { first };
        var wait = TimeSpan.FromMilliseconds(_config.AlbumWaitMilliseconds);
        QueuedUpdate? other = null;

        while (other is null)
        {
            using var quiet = new CancellationTokenSource(wait, _time);
            QueuedUpdate next;
            try
            {
                // A cancelled read takes nothing from the queue.
                next = await partition.Reader.ReadAsync(quiet.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }

            if (next.Update.Message is { } message && message.MediaGroupId == groupId)
                items.Add(message);
            else
                other = next;
        }

        return (items.OrderBy(message => message.MessageId).ToArray(), other);
    }

    /// <summary>
    /// Processes a check before payment right away, with a deadline. If the deadline passes, the order is declined,
    /// so the user sees a clear message instead of a payment that silently fails.
    /// </summary>
    private async Task ProcessPreCheckoutAsync(Update update, PreCheckoutQuery checkout)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_processing.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(_config.Stars.PreCheckoutTimeoutSeconds));

        try
        {
            await _dispatcher.DispatchAsync(update, callbackAnswered: false, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // Handled below.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The check before payment {UpdateId} failed", update.Id);
        }

        if (!deadline.IsCancellationRequested || _processing.IsCancellationRequested)
            return;

        _logger.LogWarning("The check before payment of user {UserId} took longer than {Seconds}s, so the order was declined",
            checkout.From.Id, _config.Stars.PreCheckoutTimeoutSeconds);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var text = StarsMiddleware.DeclineText(scope.ServiceProvider);
            await _bot.AnswerPreCheckoutQuery(checkout.Id, errorMessage: text, cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not decline the order; it was probably answered already");
        }
    }

    private void RemoveIdlePartitions()
    {
        var now = _time.GetUtcNow();
        var ttl = TimeSpan.FromSeconds(_config.IdleChatTtlSeconds);

        foreach (var (key, partition) in _partitions)
        {
            if (partition.Count > 0 || now - partition.LastSeen <= ttl)
                continue;

            if (_partitions.TryRemove(new KeyValuePair<long, Partition>(key, partition)))
            {
                partition.Complete();
                _logger.LogDebug("Queue removed after being idle. Key={Key}", key);
            }
        }
    }

    private async Task<bool> TryAnswerButtonAsync(CallbackQuery query, CancellationToken ct)
    {
        try
        {
            await _bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not answer the button press right away");
            return false;
        }
    }

    /// <summary>
    /// The queue key of an update. Updates without a chat fall back to the user, and the other way round.
    /// </summary>
    private long GetKey(Update update)
    {
        var chatId = UpdateDescriber.GetChat(update)?.Id;
        var userId = UpdateDescriber.GetSender(update)?.Id;

        return _keyStrategy switch
        {
            QueueKeyStrategy.User => userId ?? chatId ?? 0,
            QueueKeyStrategy.ChatAndUser when chatId is { } chat && userId is { } user => Combine(chat, user),
            _ => chatId ?? userId ?? 0
        };
    }

    /// <summary>
    /// The name of a conversation for <see cref="IConversationLock"/>: the same in every instance of the bot.
    /// </summary>
    private string LockKey(long key) => _keyStrategy switch
    {
        QueueKeyStrategy.User => "user:",
        QueueKeyStrategy.ChatAndUser => "chat-user:",
        _ => "chat:"
    } + key.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Combines a chat id and a user id into one key. A rare collision only means two conversations share a queue.
    /// </summary>
    private static long Combine(long chatId, long userId)
    {
        unchecked
        {
            return (long)(((ulong)chatId * 11400714819323198485UL) ^ ((ulong)userId + 0x9E3779B97F4A7C15UL));
        }
    }

    /// <summary>
    /// The queue of one key and its consumers.
    /// </summary>
    private sealed class Partition
    {
        private readonly Channel<QueuedUpdate> _channel;
        private long _lastSeenTicks;

        public Partition(long key, Channel<QueuedUpdate> channel, DateTimeOffset now)
        {
            Key = key;
            _channel = channel;
            Touch(now);
        }

        public long Key { get; }

        public Task[] Consumers { get; set; } = [];

        public ChannelReader<QueuedUpdate> Reader => _channel.Reader;

        public int Count => _channel.Reader.Count;

        public DateTimeOffset LastSeen => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);

        public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastSeenTicks, now.UtcTicks);

        /// <summary>
        /// Writes the update. Returns <c>false</c> only when the queue was closed.
        /// </summary>
        public async ValueTask<bool> TryWriteAsync(QueuedUpdate item, bool dropOnOverflow, CancellationToken ct)
        {
            // With DropOldest a write always succeeds while the queue is open; the dropped update is logged.
            if (dropOnOverflow)
                return _channel.Writer.TryWrite(item);

            try
            {
                await _channel.Writer.WriteAsync(item, ct);
                return true;
            }
            catch (ChannelClosedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Closes the queue: consumers finish what is already in it and stop.
        /// </summary>
        public void Complete() => _channel.Writer.TryComplete();
    }
}
