using TelegramBotForge.Logging;
using TelegramBotForge.Results;

namespace TelegramBotForge.Routing;

/// <summary>
/// The end of the pipeline: picks the first action that matches the update and runs it.
/// </summary>
internal sealed class BotRouter
{
    private readonly ActionRegistry _registry;

    public BotRouter(ActionRegistry registry) => _registry = registry;

    public async Task<IBotResult?> RouteAsync(BotContext context, CancellationToken ct)
    {
        if (context.State?.Current is { } state)
            UpdateTrace.SetState(state);

        foreach (var action in _registry.Actions)
        {
            ct.ThrowIfCancellationRequested();
            if (!action.TryMatch(context, out var values))
                continue;

            UpdateTrace.Mark(action.DisplayName);
            context.HandledBy = action.DisplayName;
            return await action.InvokeAsync(context, values);
        }

        // Text always reaches an action: the built-in unknown-command action is tried last.
        // Other updates without an action are ignored.
        UpdateTrace.Mark("no action");
        return null;
    }
}
