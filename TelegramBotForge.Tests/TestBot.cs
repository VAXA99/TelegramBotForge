using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types.Enums;
using TelegramBotForge.Controllers;
using TelegramBotForge.Payments;
using TelegramBotForge.Results;
using TelegramBotForge.Testing;
using TelegramBotForge.Views;

namespace TelegramBotForge.Tests;

// The bot every test runs: all controllers of this assembly are found, so they must not conflict.

public sealed class StartController : BotController
{
    [Command("start", Description = "Start the bot")]
    public IBotResult Start(string? args) => args is null ? Reply("Hello!") : Reply($"Invited by {args}");

    [Command("echo")]
    public IBotResult Echo(string[] args) => Reply(args.Length == 0 ? "(nothing)" : string.Join("|", args));
}

public sealed class MenuView : View
{
    protected override void Build(MessageBuilder message) => message
        .Line($"Main menu")
        .Row(Button("Catalog", "catalog:1"));
}

public sealed class MenuController(MenuView menu) : BotController
{
    [Command("menu", Description = "Main menu")]
    public IBotResult Menu() => Reply(menu.Render());

    [Callback("catalog:{page:int}")]
    public IBotResult Catalog(int page) => Edit($"Page {page}");
}

public enum Signup { Name, Age }

public sealed class SignupController : BotController
{
    [Command("signup")]
    public IBotResult Start() => EnterState(Signup.Name, "What's your name?");

    [State(Signup.Name), Text]
    public IBotResult OnName(string text)
    {
        State.Set("name", text);
        return EnterState(Signup.Age, "How old are you?");
    }

    [State(Signup.Age), Text]
    public IBotResult OnAge(string text) =>
        int.TryParse(text, out _) ? ExitState($"Thanks, {State.Get<string>("name")}!") : Reply("Please send a number.");
}

public interface IAdminList
{
    bool IsAdmin(long? userId);
}

public sealed class AdminList(params long[] admins) : IAdminList
{
    public bool IsAdmin(long? userId) => userId is { } id && admins.Contains(id);
}

public sealed class AdminOnlyFilter(IAdminList admins) : IBotActionFilter
{
    public async Task<IBotResult?> OnActionAsync(BotActionContext action, BotActionNext next, CancellationToken ct) =>
        admins.IsAdmin(action.Context.UserId) ? await next() : new SendText("Admins only.");
}

public sealed class ShoutAttribute : BotFilterAttribute
{
    public override async Task<IBotResult?> OnActionAsync(BotActionContext action, BotActionNext next, CancellationToken ct) =>
        await next() is SendText send ? send with { Text = send.Text.ToUpperInvariant() } : null;
}

[UseFilter<AdminOnlyFilter>]
public sealed class AdminController : BotController
{
    [Command("ban", Description = "Ban a user")]
    [ChatType(ChatType.Private)]
    public IBotResult Ban(string? args) => Reply($"Banned {args}");

    [Command("stats")]
    [Shout]
    public IBotResult Stats() => Reply("all good");
}

public sealed class WhereController : BotController
{
    [Command("where")]
    [ChatType(ChatType.Private)]
    public IBotResult Private() => Reply("private");

    [Command("where")]
    [ChatType(ChatType.Group, ChatType.Supergroup)]
    public IBotResult Group() => Reply("group");

    [Command("poll", Description = "Start a poll")]
    [ChatType(ChatType.Group, ChatType.Supergroup)]
    public IBotResult Poll() => Reply("poll started");
}

public sealed class LanguageController : BotController
{
    [Command("lang")]
    public IBotResult Lang(CultureInfo culture) => Reply($"{culture.Name}|{CultureInfo.CurrentUICulture.Name}");
}

public sealed class PaymentLog
{
    public List<string> Paid { get; } = [];
}

public sealed class ShopController(PaymentLog log) : BotController
{
    [Callback("buy")]
    public IBotResult Buy() => Invoice("premium");

    [Paid("premium")]
    public IBotResult OnPaid(StarsPayment payment)
    {
        log.Paid.Add(payment.ChargeId);
        return Reply("Premium is active");
    }
}

public sealed class CountingFilter(Counter counter) : IBotActionFilter
{
    public Task<IBotResult?> OnActionAsync(BotActionContext action, BotActionNext next, CancellationToken ct)
    {
        counter.Actions.Add(action.ActionName);
        return next();
    }
}

public sealed class Counter
{
    public List<string> Actions { get; } = [];
}

internal static class TestBot
{
    public const long Admin = 1;

    public static BotTestHost Create(
        Action<IServiceCollection>? services = null,
        Action<TelegramBotBuilder>? bot = null,
        IDictionary<string, string?>? configuration = null) =>
        BotTestHost.Create<StartController>(
            s =>
            {
                s.AddSingleton<IAdminList>(new AdminList(Admin));
                s.AddSingleton<PaymentLog>();
                s.AddSingleton<Counter>();
                services?.Invoke(s);
            },
            b =>
            {
                b.AddStars(stars => stars.Product("premium", "Premium", "30 days of Premium", price: 50));
                bot?.Invoke(b);
            },
            new Dictionary<string, string?>(configuration ?? new Dictionary<string, string?>())
            {
                ["Stars:PaySupportMessage"] = "Write to @support."
            });
}
