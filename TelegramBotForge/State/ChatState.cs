using System.Text.Json;

namespace TelegramBotForge.State;

/// <summary>
/// Identifies one conversation: a user in a chat. Two people filling a form in the same group have separate states.
/// </summary>
/// <param name="ChatId">The chat id.</param>
/// <param name="UserId">The user id.</param>
public readonly record struct StateKey(long ChatId, long UserId);

/// <summary>
/// The conversation state of a user in a chat: the current state (for example the step of a form)
/// and the data collected so far. Kept as plain strings, so any storage can save it.
/// </summary>
public sealed class ChatState
{
    private readonly Dictionary<string, string> _data;
    private string? _current;

    /// <summary>
    /// Initializes a new, empty state.
    /// </summary>
    public ChatState()
        : this(null, new Dictionary<string, string>(), DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// Initializes a state loaded from storage.
    /// </summary>
    /// <param name="current">The current state name, or <c>null</c>.</param>
    /// <param name="data">The collected data.</param>
    /// <param name="updatedAt">When the state was last changed.</param>
    public ChatState(string? current, IReadOnlyDictionary<string, string> data, DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(data);
        _current = current;
        _data = new Dictionary<string, string>(data);
        UpdatedAt = updatedAt;
    }

    /// <summary>
    /// Gets or sets the current state name, for example <c>Registration.Age</c>; <c>null</c> when the user is in no conversation.
    /// Actions set it with <c>EnterState</c>; <c>[State]</c> routes match it.
    /// </summary>
    public string? Current
    {
        get => _current;
        set
        {
            _current = value;
            IsModified = true;
        }
    }

    /// <summary>
    /// Gets the collected data as JSON strings.
    /// </summary>
    public IReadOnlyDictionary<string, string> Data => _data;

    /// <summary>
    /// Gets when the state was last changed.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; internal set; }

    /// <summary>
    /// Gets a value indicating whether there is no current state and no data.
    /// </summary>
    public bool IsEmpty => _current is null && _data.Count == 0;

    /// <summary>
    /// Gets a value indicating whether the state changed during this update and has to be saved.
    /// </summary>
    internal bool IsModified { get; private set; }

    /// <summary>
    /// Stores a value, for example an answer of a form.
    /// </summary>
    public void Set<T>(string key, T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        _data[key] = JsonSerializer.Serialize(value);
        IsModified = true;
    }

    /// <summary>
    /// Gets a stored value, or <c>default</c> when it is missing.
    /// </summary>
    public T? Get<T>(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;
    }

    /// <summary>
    /// Removes a stored value.
    /// </summary>
    /// <returns><c>true</c> if the value existed.</returns>
    public bool Remove(string key)
    {
        var removed = _data.Remove(key);
        IsModified |= removed;
        return removed;
    }

    /// <summary>
    /// Ends the conversation: clears the current state and all data.
    /// </summary>
    public void Clear()
    {
        if (IsEmpty)
            return;

        _current = null;
        _data.Clear();
        IsModified = true;
    }

    internal ChatState Clone() => new(_current, _data, UpdatedAt);
}

/// <summary>
/// Turns state values such as enum members into the names stored in <see cref="ChatState.Current"/>.
/// </summary>
internal static class StateNames
{
    /// <summary>
    /// <c>Registration.Age</c> becomes <c>"Registration.Age"</c>. The enum type name keeps two enums with the same member names apart.
    /// </summary>
    public static string Of(object state) => state switch
    {
        Enum value => $"{value.GetType().Name}.{value}",
        string name when !string.IsNullOrWhiteSpace(name) => name,
        _ => throw new ArgumentException($"A state must be an enum value or a non-empty string, but was {state?.GetType().Name ?? "null"}.")
    };
}
