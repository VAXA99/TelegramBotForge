using TelegramBotForge.Results;
using TelegramBotForge.State;

namespace TelegramBotForge.Middleware;

/// <summary>
/// Built-in middleware, always last before the router. Loads the conversation state of the chat and user before routing,
/// because <c>[State]</c> actions depend on it, and saves or deletes it after the action.
/// If the action fails, its changes to the state are not saved.
/// </summary>
public sealed class StateMiddleware : IBotMiddleware
{
    private readonly IStateStorage _storage;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeToLive;

    /// <summary>
    /// Initializes a new instance of the <see cref="StateMiddleware"/> class.
    /// </summary>
    /// <param name="storage">Where states are kept.</param>
    /// <param name="bot">The bot builder; its <see cref="TelegramBotBuilder.KeepStateFor"/> time is used.</param>
    /// <param name="timeProvider">The clock. Defaults to the system clock.</param>
    public StateMiddleware(IStateStorage storage, TelegramBotBuilder bot, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(bot);
        _storage = storage;
        _time = timeProvider ?? TimeProvider.System;
        _timeToLive = bot.StateTimeToLive;
    }

    /// <inheritdoc/>
    public async Task<IBotResult?> InvokeAsync(BotContext context, BotNext next, CancellationToken ct)
    {
        if (context.ChatId is not { } chatId || context.UserId is not { } userId)
            return await next(context, ct);

        var key = new StateKey(chatId, userId);
        var state = await _storage.GetAsync(key, ct) ?? new ChatState();
        context.State = state;

        var result = await next(context, ct);

        if (state.IsModified)
        {
            if (state.IsEmpty)
            {
                await _storage.RemoveAsync(key, ct);
            }
            else
            {
                state.UpdatedAt = _time.GetUtcNow();
                await _storage.SaveAsync(key, state, _timeToLive, ct);
            }
        }

        return result;
    }
}
