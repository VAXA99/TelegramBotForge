using System.Collections.Concurrent;

namespace TelegramBotForge.State;

/// <summary>
/// Where conversation states are kept. The default keeps them in memory; register your own,
/// for example with Redis or a database, with <c>UseStateStorage&lt;T&gt;()</c>.
/// </summary>
public interface IStateStorage
{
    /// <summary>
    /// Loads the state, or returns <c>null</c> when there is none or it has expired.
    /// </summary>
    ValueTask<ChatState?> GetAsync(StateKey key, CancellationToken ct);

    /// <summary>
    /// Saves the state. It expires <paramref name="timeToLive"/> after this call unless it is saved again.
    /// </summary>
    ValueTask SaveAsync(StateKey key, ChatState state, TimeSpan timeToLive, CancellationToken ct);

    /// <summary>
    /// Deletes the state.
    /// </summary>
    ValueTask RemoveAsync(StateKey key, CancellationToken ct);
}

/// <summary>
/// Keeps states in memory. Fast and needs no setup, but everything is lost on restart
/// and it only works with a single running instance of the bot.
/// </summary>
public sealed class InMemoryStateStorage : IStateStorage
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<StateKey, Entry> _states = new();
    private readonly TimeProvider _time;
    private DateTimeOffset _nextSweep;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryStateStorage"/> class.
    /// </summary>
    /// <param name="timeProvider">The clock. Defaults to the system clock.</param>
    public InMemoryStateStorage(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _nextSweep = _time.GetUtcNow() + SweepInterval;
    }

    /// <inheritdoc/>
    public ValueTask<ChatState?> GetAsync(StateKey key, CancellationToken ct)
    {
        if (!_states.TryGetValue(key, out var entry))
            return ValueTask.FromResult<ChatState?>(null);

        if (entry.ExpiresAt <= _time.GetUtcNow())
        {
            _states.TryRemove(new KeyValuePair<StateKey, Entry>(key, entry));
            return ValueTask.FromResult<ChatState?>(null);
        }

        // A copy, so changes only count once they are saved, like with an external storage.
        return ValueTask.FromResult<ChatState?>(entry.State.Clone());
    }

    /// <inheritdoc/>
    public ValueTask SaveAsync(StateKey key, ChatState state, TimeSpan timeToLive, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        var now = _time.GetUtcNow();
        _states[key] = new Entry(state.Clone(), now + timeToLive);
        SweepIfDue(now);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask RemoveAsync(StateKey key, CancellationToken ct)
    {
        _states.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }

    private void SweepIfDue(DateTimeOffset now)
    {
        if (now < _nextSweep)
            return;

        _nextSweep = now + SweepInterval;
        foreach (var pair in _states)
        {
            if (pair.Value.ExpiresAt <= now)
                _states.TryRemove(pair);
        }
    }

    private sealed record Entry(ChatState State, DateTimeOffset ExpiresAt);
}
