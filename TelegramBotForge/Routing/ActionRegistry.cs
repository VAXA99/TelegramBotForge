using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TelegramBotForge.Controllers;
using TelegramBotForge.Results;

namespace TelegramBotForge.Routing;

/// <summary>
/// One action: a controller method with route attributes, prepared at startup.
/// At runtime nothing is discovered with reflection anymore; the method is only invoked.
/// </summary>
internal sealed class ActionDescriptor
{
    private static readonly IReadOnlyDictionary<string, object?> NoValues = new Dictionary<string, object?>();

    private readonly ObjectFactory _controllerFactory;
    private readonly ParameterBinder[] _binders;
    private readonly Func<object?, Task<IBotResult?>> _adaptResult;
    private readonly IReadOnlyDictionary<string, RouteParameter> _routeParameters;
    private readonly HashSet<string>? _states;

    public ActionDescriptor(
        Type controllerType,
        MethodInfo method,
        IReadOnlyList<RouteAttribute> routes,
        IReadOnlyList<string>? states,
        ParameterBinder[] binders,
        IReadOnlyDictionary<string, RouteParameter> routeParameters,
        Func<object?, Task<IBotResult?>> adaptResult)
    {
        ControllerType = controllerType;
        Method = method;
        Routes = routes;
        States = states;
        _states = states is null ? null : new HashSet<string>(states, StringComparer.Ordinal);
        _binders = binders;
        _routeParameters = routeParameters;
        _adaptResult = adaptResult;
        _controllerFactory = ActivatorUtilities.CreateFactory(controllerType, Type.EmptyTypes);

        // Actions bound to a state beat general ones; commands (100) still beat state actions (50-60), so /cancel always works.
        Priority = routes.Max(route => route.Priority) + (states is null ? 0 : 50);
    }

    public Type ControllerType { get; }

    public MethodInfo Method { get; }

    public IReadOnlyList<RouteAttribute> Routes { get; }

    public IReadOnlyList<string>? States { get; }

    public int Priority { get; }

    public string DisplayName => $"{ControllerType.Name}.{Method.Name}";

    public bool TryMatch(BotContext context, out IReadOnlyDictionary<string, object?> values)
    {
        values = NoValues;

        if (_states is not null && (context.State?.Current is not { } current || !_states.Contains(current)))
            return false;

        foreach (var route in Routes)
        {
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (route.TryMatch(context, raw) && TryConvert(raw, out values))
                return true;
        }

        return false;
    }

    public async Task<IBotResult?> InvokeAsync(BotContext context, IReadOnlyDictionary<string, object?> values)
    {
        var controller = (BotController)_controllerFactory(context.Services, null);
        controller.Context = context;

        try
        {
            var args = new object?[_binders.Length];
            for (var i = 0; i < args.Length; i++)
                args[i] = _binders[i](context, values);

            var returned = Method.Invoke(controller, BindingFlags.DoNotWrapExceptions, null, args, null);
            return await _adaptResult(returned);
        }
        finally
        {
            if (controller is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync();
            else if (controller is IDisposable disposable)
                disposable.Dispose();
        }
    }

    /// <summary>
    /// Converts the matched {placeholder} values. A value that does not convert means the route does not match.
    /// A placeholder the matched route does not have (a command next to <c>catalog:{page}</c>) gets the parameter's default value.
    /// </summary>
    private bool TryConvert(Dictionary<string, string> raw, out IReadOnlyDictionary<string, object?> values)
    {
        if (_routeParameters.Count == 0)
        {
            values = NoValues;
            return true;
        }

        var converted = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, parameter) in _routeParameters)
        {
            if (!raw.TryGetValue(name, out var text))
            {
                // Checked at startup: only optional parameters can be missing from a route.
                converted[name] = parameter.DefaultValue;
                continue;
            }

            if (!ValueConverter.TryConvert(text, parameter.Type, out var value))
            {
                values = NoValues;
                return false;
            }

            converted[name] = value;
        }

        values = converted;
        return true;
    }
}

/// <summary>
/// All actions of the bot, sorted by priority. Built and checked once at startup:
/// any problem stops the bot with a message that lists every one of them.
/// </summary>
internal sealed class ActionRegistry
{
    private ActionRegistry(IReadOnlyList<ActionDescriptor> actions) => Actions = actions;

    public IReadOnlyList<ActionDescriptor> Actions { get; }

    /// <param name="assemblies">The assemblies with your controllers.</param>
    /// <param name="builtInControllers">Controllers of the library, such as the <c>/paysupport</c> answer.
    /// An action of yours with the same route replaces the built-in one.</param>
    public static ActionRegistry Build(IEnumerable<Assembly> assemblies, IEnumerable<Type>? builtInControllers = null)
    {
        var errors = new List<string>();
        var actions = new List<ActionDescriptor>();

        var controllerTypes = assemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } &&
                           type.IsSubclassOf(typeof(BotController)))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        foreach (var controllerType in controllerTypes)
        {
            var methods = controllerType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.DeclaringType is { } declaring && declaring.IsSubclassOf(typeof(BotController)));

            foreach (var method in methods)
            {
                if (TryCreateAction(controllerType, method, errors) is { } action)
                    actions.Add(action);
            }
        }

        // Built-in actions step aside for your own actions with the same route.
        var taken = actions.SelectMany(action => action.Routes.Select(route => RouteKey(action, route))).ToHashSet(StringComparer.Ordinal);
        foreach (var controllerType in builtInControllers ?? Enumerable.Empty<Type>())
        {
            foreach (var method in controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (TryCreateAction(controllerType, method, errors) is { } action &&
                    !action.Routes.Any(route => taken.Contains(RouteKey(action, route))))
                {
                    actions.Add(action);
                }
            }
        }

        FindDuplicates(actions, errors);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "TelegramBotForge found problems in the controllers:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(error => " - " + error)));
        }

        // OrderByDescending is stable: actions with the same priority keep their order.
        return new ActionRegistry(actions.OrderByDescending(action => action.Priority).ToList());
    }

    private static ActionDescriptor? TryCreateAction(Type controllerType, MethodInfo method, List<string> errors)
    {
        var name = $"{controllerType.Name}.{method.Name}";
        List<RouteAttribute> routes;
        StateAttribute? state;

        try
        {
            routes = method.GetCustomAttributes<RouteAttribute>(inherit: true).ToList();
            state = method.GetCustomAttribute<StateAttribute>(inherit: true);
        }
        catch (Exception ex)
        {
            // Attribute constructors throw for invalid commands, templates or states.
            errors.Add($"{name}: {(ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message)}");
            return null;
        }

        if (routes.Count == 0 && state is null)
            return null;

        // [State] alone: any message in that state.
        if (routes.Count == 0)
            routes.Add(AnyMessageRoute.Instance);

        var errorCount = errors.Count;

        if (method.IsGenericMethodDefinition)
            errors.Add($"{name}: an action cannot be a generic method.");

        // The placeholders of all routes together. One that only some routes have, like {page} in
        // [Command("catalog")] + [Callback("catalog:{page:int}")], must have a default value for the other routes.
        var placeholders = new HashSet<string>(routes.SelectMany(route => route.ParameterNames), StringComparer.OrdinalIgnoreCase);
        var partial = placeholders
            .Where(placeholder => routes.Any(route => !route.ParameterNames.Contains(placeholder, StringComparer.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var binders = new List<ParameterBinder>();
        var routeParameters = new Dictionary<string, RouteParameter>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in method.GetParameters())
        {
            if (ParameterBinding.TryCreate(parameter, placeholders, out var binder, out var error))
            {
                binders.Add(binder);
                if (!placeholders.Contains(parameter.Name!))
                    continue;

                routeParameters[parameter.Name!] = new RouteParameter(parameter.ParameterType, parameter.HasDefaultValue ? parameter.DefaultValue : null);
                if (partial.Contains(parameter.Name!) && !parameter.HasDefaultValue)
                {
                    errors.Add($"{name}: {{{parameter.Name}}} is not in every route of the action, so '{parameter.Name}' needs a default value " +
                               $"for the routes without it, for example '{parameter.ParameterType.Name} {parameter.Name} = ...'.");
                }
            }
            else
            {
                errors.Add($"{name}: {error}");
            }
        }

        foreach (var placeholder in placeholders.Where(placeholder => !routeParameters.ContainsKey(placeholder)))
            errors.Add($"{name}: {{{placeholder}}} has no parameter with that name.");

        var adapter = ResultAdapter.TryCreate(method, out var returnError);
        if (adapter is null)
            errors.Add($"{name}: {returnError}");

        if (errors.Count > errorCount || adapter is null)
            return null;

        return new ActionDescriptor(controllerType, method, routes, state?.States, binders.ToArray(), routeParameters, adapter);
    }

    private static void FindDuplicates(List<ActionDescriptor> actions, List<string> errors)
    {
        var duplicates = actions
            .SelectMany(action => action.Routes.Select(route => (Key: RouteKey(action, route), Action: action.DisplayName)))
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);

        foreach (var group in duplicates)
            errors.Add($"{string.Join(" and ", group.Select(entry => entry.Action))} have the same route ({group.Key}).");
    }

    private static string RouteKey(ActionDescriptor action, RouteAttribute route) =>
        route.Key + (action.States is null ? string.Empty : " in " + string.Join(", ", action.States));
}

/// <summary>
/// An action parameter filled from a {placeholder}, with the value used when the matched route does not have it.
/// </summary>
internal readonly record struct RouteParameter(Type Type, object? DefaultValue);

/// <summary>
/// The route of an action that only has <see cref="StateAttribute"/>: any message.
/// </summary>
internal sealed class AnyMessageRoute : RouteAttribute
{
    public static readonly AnyMessageRoute Instance = new();

    internal override int Priority => 0;

    internal override string Key => "any message";

    // Payment notices are messages too, but they only go to [Paid] actions, never to a form step.
    internal override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.Message is { SuccessfulPayment: null };
}
