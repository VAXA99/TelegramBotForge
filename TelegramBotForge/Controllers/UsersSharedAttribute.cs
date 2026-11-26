namespace TelegramBotForge.Controllers;

/// <summary>Routes a users-selection response. Like contact/location input, priority is 20, plus any state priority.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class UsersSharedAttribute : RouteAttribute
{
    public override int Priority => 20;
    public override string Key => "users shared";
    public override bool TryMatch(BotContext context, IDictionary<string, string> values) => context.UsersShared is not null;
}
