using TelegramBotForge.Results;
using TelegramBotForge.Views;

namespace TelegramBotForge.Controllers;

/// <summary>
/// Turns a rendered view into a result: a text message, or a media message when it has media.
/// </summary>
internal static class ViewResults
{
    public static IBotResult From(BotMessage message) =>
        message.Media is null ? SendText.From(message) : SendMedia.From(message);

    public static IBotResult? From(SystemView view) => view.RenderOrNull() is { } message ? From(message) : null;
}

/// <summary>
/// The built-in answer to text that no action handles. It is the last action the router tries,
/// so any action of yours that matches comes first.
/// </summary>
internal sealed class UnknownCommandController : BotController
{
    private readonly UnknownCommandView _view;

    public UnknownCommandController(UnknownCommandView view) => _view = view;

    [UnknownText]
    public IBotResult? UnknownCommand() => ViewResults.From(_view);
}

/// <summary>
/// Matches any text message, after every other action.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class UnknownTextAttribute : RouteAttribute
{
    internal override int Priority => -10;

    internal override string Key => "unknown text";

    internal override bool TryMatch(BotContext context, IDictionary<string, string> values) => context.Text is not null;
}
