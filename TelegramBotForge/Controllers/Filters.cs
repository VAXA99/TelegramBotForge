using System.Reflection;
using Telegram.Bot.Types.Enums;
using TelegramBotForge.Results;

namespace TelegramBotForge.Controllers;

/// <summary>
/// The action a filter runs around.
/// </summary>
public sealed class BotActionContext
{
    internal BotActionContext(BotContext context, Type controllerType, MethodInfo method)
    {
        Context = context;
        ControllerType = controllerType;
        Method = method;
    }

    /// <summary>
    /// Gets the context of the update.
    /// </summary>
    public BotContext Context { get; }

    /// <summary>
    /// Gets the controller of the action.
    /// </summary>
    public Type ControllerType { get; }

    /// <summary>
    /// Gets the method of the action, for example to read your own attributes from it.
    /// </summary>
    public MethodInfo Method { get; }

    /// <summary>
    /// Gets the name of the action, such as <c>AdminController.Ban</c>.
    /// </summary>
    public string ActionName => $"{ControllerType.Name}.{Method.Name}";
}

/// <summary>
/// Runs the rest of the filters and then the action.
/// </summary>
/// <returns>The result of the action, or of a later filter that stopped it.</returns>
public delegate Task<IBotResult?> BotActionNext();

/// <summary>
/// Code that runs around chosen actions, after routing: authorization, "only for subscribers", auditing, timing.
/// Unlike middleware, a filter knows which action runs and is attached only where it is needed.
/// To stop the action, return a result (or <c>null</c>) without calling <c>next</c>.
/// </summary>
/// <remarks>
/// Attach a filter to a controller or an action with <see cref="UseFilterAttribute{TFilter}"/> (it is created from DI,
/// so it can take services), derive a simple one from <see cref="BotFilterAttribute"/>, or add it to every action with
/// <c>AddFilter&lt;T&gt;()</c> at startup. They run in that order: the ones added at startup, then the controller's, then the action's.
/// Filters added at startup skip <c>[PreCheckout]</c> and <c>[Paid]</c> actions, so a payment is never lost to them.
/// </remarks>
/// <example>
/// <code>
/// public sealed class AdminOnlyFilter(IAdminList admins) : IBotActionFilter
/// {
///     public async Task&lt;IBotResult?&gt; OnActionAsync(BotActionContext action, BotActionNext next, CancellationToken ct) =>
///         await admins.ContainsAsync(action.Context.UserId, ct) ? await next() : new SendText("Admins only.");
/// }
///
/// [UseFilter&lt;AdminOnlyFilter&gt;]
/// public sealed class AdminController : BotController { ... }
/// </code>
/// </example>
public interface IBotActionFilter
{
    /// <summary>
    /// Runs around the action.
    /// </summary>
    /// <param name="action">The action and its update.</param>
    /// <param name="next">Runs the rest of the filters and the action.</param>
    /// <param name="ct">A token to cancel processing of the update.</param>
    /// <returns>The result to send.</returns>
    Task<IBotResult?> OnActionAsync(BotActionContext action, BotActionNext next, CancellationToken ct);
}

/// <summary>
/// A filter written as an attribute, for filters that need no services
/// (they can still get them from <c>action.Context.Services</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public abstract class BotFilterAttribute : Attribute, IBotActionFilter
{
    /// <summary>
    /// Gets or sets the order among the filters of the same controller or action; lower runs first.
    /// </summary>
    public int Order { get; set; }

    /// <inheritdoc/>
    public abstract Task<IBotResult?> OnActionAsync(BotActionContext action, BotActionNext next, CancellationToken ct);
}

/// <summary>
/// Runs a filter of type <see cref="FilterType"/>, created from DI, around the actions of this controller or this action.
/// Prefer <see cref="UseFilterAttribute{TFilter}"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public class UseFilterAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UseFilterAttribute"/> class.
    /// </summary>
    /// <param name="filterType">A class implementing <see cref="IBotActionFilter"/>.</param>
    public UseFilterAttribute(Type filterType)
    {
        ArgumentNullException.ThrowIfNull(filterType);
        if (!typeof(IBotActionFilter).IsAssignableFrom(filterType) || filterType.IsAbstract || filterType.IsInterface)
            throw new ArgumentException($"{filterType.Name} is not a class implementing {nameof(IBotActionFilter)}.", nameof(filterType));
        FilterType = filterType;
    }

    /// <summary>
    /// Gets the filter type.
    /// </summary>
    public Type FilterType { get; }

    /// <summary>
    /// Gets or sets the order among the filters of the same controller or action; lower runs first.
    /// </summary>
    public int Order { get; set; }
}

/// <summary>
/// Runs <typeparamref name="TFilter"/>, created from DI, around the actions of this controller or this action.
/// </summary>
/// <typeparam name="TFilter">The filter.</typeparam>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class UseFilterAttribute<TFilter> : UseFilterAttribute where TFilter : class, IBotActionFilter
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UseFilterAttribute{TFilter}"/> class.
    /// </summary>
    public UseFilterAttribute()
        : base(typeof(TFilter))
    {
    }
}

/// <summary>
/// A condition an update must meet for the action to match. Unlike a filter, an update that does not meet it
/// goes on to the next action that matches, so the same command can have one action for private chats and another for groups.
/// Derive from it for your own conditions.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public abstract class BotConstraintAttribute : Attribute
{
    /// <summary>
    /// Checks the update. Called for every update that reaches the action's routes, so keep it fast and synchronous.
    /// </summary>
    public abstract bool IsMatch(BotContext context);

    /// <summary>
    /// Gets a short text that tells this condition apart, used to find two actions with the same route
    /// and the same conditions at startup. By default, the type name.
    /// </summary>
    public virtual string Key => GetType().Name;
}

/// <summary>
/// Matches only in chats of these types, for example <c>[ChatType(ChatType.Private)]</c>.
/// With <c>RegisterCommands()</c>, a command limited to private chats or to groups appears only in that menu.
/// </summary>
public sealed class ChatTypeAttribute : BotConstraintAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChatTypeAttribute"/> class.
    /// </summary>
    /// <param name="types">The chat types the action works in.</param>
    public ChatTypeAttribute(params ChatType[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (types.Length == 0)
            throw new ArgumentException("[ChatType] needs at least one chat type.", nameof(types));
        Types = types.Distinct().Order().ToArray();
    }

    /// <summary>
    /// Gets the chat types.
    /// </summary>
    public IReadOnlyList<ChatType> Types { get; }

    /// <inheritdoc/>
    public override bool IsMatch(BotContext context) => context.Chat is { } chat && Types.Contains(chat.Type);

    /// <inheritdoc/>
    public override string Key => "chat type " + string.Join("/", Types);
}

/// <summary>
/// Gets an action parameter from DI, for a service only one action needs. Registered services are found without it too;
/// use it to make that explicit. ASP.NET Core's <c>[FromServices]</c> works the same way.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class FromServicesAttribute : Attribute
{
}
