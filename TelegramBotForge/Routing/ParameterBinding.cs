using System.Globalization;
using System.Reflection;
using Telegram.Bot.Types;
using TelegramBotForge.Media;
using TelegramBotForge.Payments;
using TelegramBotForge.Results;

namespace TelegramBotForge.Routing;

/// <summary>
/// Gets the value of one action parameter for an update.
/// </summary>
internal delegate object? ParameterBinder(BotContext context, IReadOnlyDictionary<string, object?> routeValues);

/// <summary>
/// Decides at startup where each action parameter comes from.
/// </summary>
internal static class ParameterBinding
{
    public static bool TryCreate(
        ParameterInfo parameter,
        IReadOnlySet<string> placeholders,
        out ParameterBinder binder,
        out string? error,
        Microsoft.Extensions.DependencyInjection.IServiceProviderIsService? isService = null)
    {
        var type = parameter.ParameterType;
        var name = parameter.Name ?? string.Empty;
        error = null;

        if (placeholders.Contains(name))
        {
            binder = (_, values) => values[name];
            if (ValueConverter.IsSupported(type))
                return true;

            error = $"parameter '{name}' is bound to {{{name}}}, but {type.Name} cannot be read from button data. " +
                    "Use string, a number, bool, Guid or an enum.";
            return false;
        }

        // [FromServices] (ours or ASP.NET Core's): from the update's DI scope.
        if (parameter.GetCustomAttributes(inherit: true).Any(attribute => attribute.GetType().Name == "FromServicesAttribute"))
        {
            binder = (context, _) => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService(context.Services, type);
            return true;
        }

        binder = type switch
        {
            _ when type == typeof(BotContext) => (context, _) => context,
            _ when type == typeof(CancellationToken) => (context, _) => context.CancellationToken,
            _ when type == typeof(Update) => (context, _) => context.Update,
            _ when type == typeof(Message) => (context, _) => context.Message ?? context.EditedMessage,
            _ when type == typeof(CallbackQuery) => (context, _) => context.CallbackQuery,
            _ when type == typeof(User) => (context, _) => context.From,
            _ when type == typeof(Chat) => (context, _) => context.Chat,
            _ when type == typeof(BotMedia) => (context, _) => context.Media,
            _ when type == typeof(Location) => (context, _) => context.Location,
            _ when type == typeof(Venue) => (context, _) => context.Message?.Venue,
            _ when type == typeof(Contact) => (context, _) => context.Contact,
            _ when type == typeof(ChatMemberUpdated) => (context, _) => context.MembershipChange,
            _ when type == typeof(InlineQuery) => (context, _) => context.InlineQuery,
            _ when type == typeof(IReadOnlyList<BotMedia>) || type == typeof(IEnumerable<BotMedia>) => (context, _) => context.Album,
            _ when type == typeof(BotMedia[]) => (context, _) => context.Album?.ToArray(),
            _ when type == typeof(IReadOnlyList<Message>) => (context, _) => context.AlbumMessages,
            _ when type == typeof(StarsOrder) => (context, _) => context.StarsOrder,
            _ when type == typeof(StarsPayment) => (context, _) => context.StarsPayment,
            _ when type == typeof(string) && name.Equals("text", StringComparison.OrdinalIgnoreCase) => (context, _) => context.Text ?? context.EditedMessage?.Text,
            _ when type == typeof(string) && name.Equals("query", StringComparison.OrdinalIgnoreCase) => (context, _) => context.InlineQuery?.Query,
            _ when type == typeof(string) && name.Equals("args", StringComparison.OrdinalIgnoreCase) => (context, _) => context.CommandArgs,
            _ when type == typeof(string[]) && name.Equals("args", StringComparison.OrdinalIgnoreCase) =>
                (context, _) => CommandText.Split(context.CommandArgs),
            _ when type == typeof(System.Globalization.CultureInfo) => (context, _) => context.Culture ?? System.Globalization.CultureInfo.CurrentUICulture,
            _ => null!
        };

        if (binder is not null)
            return true;

        // A registered service, like in ASP.NET Core controllers and minimal APIs.
        if (isService?.IsService(type) == true)
        {
            binder = (context, _) => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService(context.Services, type);
            return true;
        }

        error = $"parameter '{name}' ({type.Name}) has no source. If it is a service, register it, or put it in the controller's constructor. " +
                "Otherwise an action can take {placeholders} of its route, 'string text', 'string args' / 'string[] args' (after a command), " +
                "'string query' (inline queries), BotMedia, IReadOnlyList<BotMedia> (albums), Location, Venue, Contact, ChatMemberUpdated, InlineQuery, " +
                "StarsOrder, StarsPayment, CultureInfo, BotContext, CancellationToken, Update, Message, CallbackQuery, User or Chat.";
        return false;
    }
}

/// <summary>
/// Converts {placeholder} values into parameter types.
/// </summary>
internal static class ValueConverter
{
    public static bool IsSupported(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(string) || t.IsEnum || t == typeof(Guid) || t == typeof(bool) ||
               t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
               t == typeof(double) || t == typeof(float) || t == typeof(decimal);
    }

    public static bool TryConvert(string raw, Type type, out object? value)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        var invariant = CultureInfo.InvariantCulture;
        value = null;

        if (t == typeof(string))
        {
            value = raw;
            return true;
        }

        if (t.IsEnum)
            return Enum.TryParse(t, raw, ignoreCase: true, out value) && Enum.IsDefined(t, value!);

        bool ok;
        switch (Type.GetTypeCode(t))
        {
            case TypeCode.Boolean:
                ok = raw is "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
                value = ok;
                return ok || raw is "0" || raw.Equals("false", StringComparison.OrdinalIgnoreCase);
            case TypeCode.Int32: ok = int.TryParse(raw, NumberStyles.Integer, invariant, out var i); value = i; return ok;
            case TypeCode.Int64: ok = long.TryParse(raw, NumberStyles.Integer, invariant, out var l); value = l; return ok;
            case TypeCode.Int16: ok = short.TryParse(raw, NumberStyles.Integer, invariant, out var s); value = s; return ok;
            case TypeCode.Byte: ok = byte.TryParse(raw, NumberStyles.Integer, invariant, out var b); value = b; return ok;
            case TypeCode.Double: ok = double.TryParse(raw, NumberStyles.Float, invariant, out var d); value = d; return ok;
            case TypeCode.Single: ok = float.TryParse(raw, NumberStyles.Float, invariant, out var f); value = f; return ok;
            case TypeCode.Decimal: ok = decimal.TryParse(raw, NumberStyles.Number, invariant, out var m); value = m; return ok;
        }

        if (t == typeof(Guid) && Guid.TryParse(raw, out var guid))
        {
            value = guid;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Turns what an action returns into a result. Supported: void, IBotResult, Task and Task&lt;IBotResult&gt;.
/// </summary>
internal static class ResultAdapter
{
    public static Func<object?, Task<IBotResult?>>? TryCreate(MethodInfo method, out string? error)
    {
        var type = method.ReturnType;
        error = null;

        if (type == typeof(void))
            return _ => Task.FromResult<IBotResult?>(null);

        if (typeof(IBotResult).IsAssignableFrom(type))
            return returned => Task.FromResult((IBotResult?)returned);

        if (type == typeof(Task))
        {
            return async returned =>
            {
                await (Task)returned!;
                return null;
            };
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>) &&
            typeof(IBotResult).IsAssignableFrom(type.GenericTypeArguments[0]))
        {
            var resultProperty = type.GetProperty(nameof(Task<object>.Result))!;
            return async returned =>
            {
                var task = (Task)returned!;
                await task;
                return (IBotResult?)resultProperty.GetValue(task);
            };
        }

        error = $"returns {type.Name}. An action returns IBotResult, Task<IBotResult>, Task or void.";
        return null;
    }
}
