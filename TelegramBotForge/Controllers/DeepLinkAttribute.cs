using TelegramBotForge.DeepLinks;
using TelegramBotForge.Routing;

namespace TelegramBotForge.Controllers;

/// <summary>Matches a /start payload before general commands, in private chats and groups.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class DeepLinkAttribute : RouteAttribute
{
    private readonly RouteTemplate _template;

    public DeepLinkAttribute(string template) => _template = new RouteTemplate(template, deepLink: true);

    public string Template => _template.Template;
    public override int Priority => 200;
    public override string Key => "deep link " + Template;
    public override IReadOnlyList<string> ParameterNames => _template.ParameterNames;

    public override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        !context.IsCommandForAnotherBot &&
        string.Equals(context.Command, "start", StringComparison.OrdinalIgnoreCase) &&
        context.CommandArgs is { } payload && DeepLinkPayload.IsValid(payload) && _template.TryMatch(payload, values);
}
