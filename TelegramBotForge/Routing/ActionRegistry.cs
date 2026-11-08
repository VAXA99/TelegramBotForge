using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TelegramBotForge.Controllers;
using TelegramBotForge.Logging;
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
    private readonly BotConstraintAttribute[] _constraints;
    private readonly FilterSource[] _filters;

    public ActionDescriptor(
        Type controllerType,
        MethodInfo method,
        IReadOnlyList<RouteAttribute> routes,
        IReadOnlyList<string>? states,
        ParameterBinder[] binders,
        IReadOnlyDictionary<string, RouteParameter> routeParameters,
        Func<object?, Task<IBotResult?>> adaptResult,
        BotConstraintAttribute[]? constraints = null,
        FilterSource[]? filters = null)
    {
        ControllerType = controllerType;
        Method = method;
        Routes = routes;
        States = states;
        _constraints = constraints ?? [];
        _filters = filters ?? [];
        ConstraintKey = _constraints.Length == 0
            ? string.Empty
            : " when " + string.Join(", ", _constraints.Select(constraint => constraint.Key).Order(StringComparer.Ordinal));
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

    public IReadOnlyList<BotConstraintAttribute> Constraints => _constraints;

    /// <summary>
    /// The conditions of the action as text, so two actions with the same route but different conditions are not duplicates.
    /// </summary>
    public string ConstraintKey { get; }

    public string DisplayName => $"{ControllerType.Name}.{Method.Name}";

    public bool TryMatch(BotContext context, out IReadOnlyDictionary<string, object?> values)
    {
        values = NoValues;

        if (_states is not null && (context.State?.Current is not { } current || !_states.Contains(current)))
            return false;

        foreach (var constraint in _constraints)
        {
            if (!constraint.IsMatch(context))
                return false;
        }

        foreach (var route in Routes)
        {
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (route.TryMatch(context, raw) && TryConvert(raw, out values))
                return true;
        }

        return false;
    }

    public Task<IBotResult?> InvokeAsync(BotContext context, IReadOnlyDictionary<string, object?> values)
    {
        if (_filters.Length == 0)
            return InvokeActionAsync(context, values);

        var action = new BotActionContext(context, ControllerType, Method);
        return RunFilterAsync(0);

        async Task<IBotResult?> RunFilterAsync(int index)
        {
            if (index == _filters.Length)
                return await InvokeActionAsync(context, values);

            var filter = _filters[index];
            var calledNext = false;
            var result = await filter.Resolve(context.Services).OnActionAsync(action, () =>
            {
                calledNext = true;
                return RunFilterAsync(index + 1);
            }, context.CancellationToken);

            if (!calledNext)
                UpdateTrace.Mark("stopped by " + filter.Name);
            return result;
        }
    }

    private async Task<IBotResult?> InvokeActionAsync(BotContext context, IReadOnlyDictionary<string, object?> values)
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
    private ActionRegistry(IReadOnlyList<ActionDescriptor> actions)
    {
        Actions = actions;
        HasAlbumActions = actions.Any(action => action.Routes.Any(route => route is AlbumAttribute));
    }

    public IReadOnlyList<ActionDescriptor> Actions { get; }

    /// <summary>
    /// Gets a value indicating whether an action takes whole albums, so the queues collect album items.
    /// </summary>
    public bool HasAlbumActions { get; }

    /// <param name="assemblies">The assemblies with your controllers.</param>
    /// <param name="builtInControllers">Controllers of the library, such as the <c>/paysupport</c> answer.
    /// An action of yours with the same route replaces the built-in one.</param>
    /// <param name="globalFilters">Filters added with <c>AddFilter&lt;T&gt;()</c>, for every action except payment actions.</param>
    /// <param name="isService">Tells which parameter types are services, so actions can take them.</param>
    public static ActionRegistry Build(
        IEnumerable<Assembly> assemblies,
        IEnumerable<Type>? builtInControllers = null,
        IReadOnlyList<Type>? globalFilters = null,
        IServiceProviderIsService? isService = null)
    {
        globalFilters ??= [];
        var errors = new List<string>();
        var actions = new List<ActionDescriptor>();

        var controllerTypes = assemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } &&
                           type.IsSubclassOf(typeof(BotController)) &&
                           type.Assembly != typeof(BotController).Assembly)
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

        foreach (var controllerType in controllerTypes)
        {
            var methods = controllerType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.DeclaringType is { } declaring && declaring.IsSubclassOf(typeof(BotController)));

            foreach (var method in methods)
            {
                if (TryCreateAction(controllerType, method, globalFilters, isService, errors) is { } action)
                    actions.Add(action);
            }
        }

        // Built-in actions step aside for your own actions with the same route.
        var taken = actions.SelectMany(action => action.Routes.Select(route => RouteKey(action, route))).ToHashSet(StringComparer.Ordinal);
        foreach (var controllerType in builtInControllers ?? Enumerable.Empty<Type>())
        {
            foreach (var method in controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (TryCreateAction(controllerType, method, globalFilters, isService, errors) is { } action &&
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

    private static ActionDescriptor? TryCreateAction(
        Type controllerType, MethodInfo method, IReadOnlyList<Type> globalFilters, IServiceProviderIsService? isService, List<string> errors)
    {
        var name = $"{controllerType.Name}.{method.Name}";
        List<RouteAttribute> routes;
        StateAttribute? state;
        BotConstraintAttribute[] constraints;
        List<FilterSource> filters;

        try
        {
            routes = method.GetCustomAttributes<RouteAttribute>(inherit: true).ToList();
            state = method.GetCustomAttribute<StateAttribute>(inherit: true);
            constraints =
            [
                .. controllerType.GetCustomAttributes<BotConstraintAttribute>(inherit: true),
                .. method.GetCustomAttributes<BotConstraintAttribute>(inherit: true)
            ];
            filters = [];
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
            if (ParameterBinding.TryCreate(parameter, placeholders, out var binder, out var error, isService))
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

        var usesArgs = method.GetParameters().Any(parameter =>
            !placeholders.Contains(parameter.Name!) &&
            string.Equals(parameter.Name, "args", StringComparison.OrdinalIgnoreCase) &&
            (parameter.ParameterType == typeof(string) || parameter.ParameterType == typeof(string[])));
        if (usesArgs && !routes.Any(route => route is CommandAttribute))
            errors.Add($"{name}: 'args' is what follows a command, but the action has no [Command] route.");

        var isPayment = routes.Any(route => route is Payments.PaidAttribute or Payments.PreCheckoutAttribute);
        if (!isPayment)
            filters.AddRange(globalFilters.Select(FilterSource.FromType));
        filters.AddRange(FilterSource.From(controllerType));
        filters.AddRange(FilterSource.From(method));

        var adapter = ResultAdapter.TryCreate(method, out var returnError);
        if (adapter is null)
            errors.Add($"{name}: {returnError}");

        if (errors.Count > errorCount || adapter is null)
            return null;

        return new ActionDescriptor(controllerType, method, routes, state?.States, binders.ToArray(), routeParameters, adapter,
            constraints, filters.ToArray());
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
        route.Key + (action.States is null ? string.Empty : " in " + string.Join(", ", action.States)) + action.ConstraintKey;
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

    public override int Priority => 0;

    public override string Key => "any message";

    // Payment notices are messages too, but they only go to [Paid] actions, never to a form step.
    public override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.Message is { SuccessfulPayment: null };
}

/// <summary>
/// One filter of an action: an attribute instance, or a type created from DI for every update.
/// </summary>
internal sealed class FilterSource
{
    private readonly IBotActionFilter? _instance;
    private readonly ObjectFactory? _factory;
    private readonly Type? _type;

    private FilterSource(IBotActionFilter? instance, Type? type, string name)
    {
        _instance = instance;
        _type = type;
        _factory = type is null ? null : ActivatorUtilities.CreateFactory(type, Type.EmptyTypes);
        Name = name;
    }

    public string Name { get; }

    public static FilterSource FromType(Type type) => new(null, type, type.Name);

    /// <summary>
    /// The filters declared on a controller or an action, in their order.
    /// </summary>
    public static IEnumerable<FilterSource> From(MemberInfo member)
    {
        var attributes = member.GetCustomAttributes(inherit: true).OfType<Attribute>().ToList();
        return attributes
            .Select((attribute, index) => (attribute, index))
            .Select(entry => entry.attribute switch
            {
                BotFilterAttribute filter => (Order: filter.Order, entry.index, Source: new FilterSource(filter, null, filter.GetType().Name)),
                UseFilterAttribute use => (Order: use.Order, entry.index, Source: FromType(use.FilterType)),
                _ => (Order: 0, entry.index, Source: (FilterSource?)null)
            })
            .Where(entry => entry.Source is not null)
            .OrderBy(entry => entry.Order)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.Source!);
    }

    /// <summary>
    /// A registered filter comes from DI with its lifetime; otherwise it is created for the update with its services.
    /// </summary>
    public IBotActionFilter Resolve(IServiceProvider services) =>
        _instance ?? (IBotActionFilter)(services.GetService(_type!) ?? _factory!(services, null));
}
