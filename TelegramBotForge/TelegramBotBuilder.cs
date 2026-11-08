using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TelegramBotForge.Configurations;
using TelegramBotForge.Middleware;
using TelegramBotForge.Payments;
using TelegramBotForge.Routing;
using TelegramBotForge.State;
using TelegramBotForge.Views;

namespace TelegramBotForge;

/// <summary>
/// Configures how the bot behaves: callback answers, queues, conversation state, the pipeline and where controllers and views are found.
/// These are decisions your code relies on, so they are made here and not in configuration files.
/// Returned by <see cref="DependencyInjection.AddTelegramBot"/>.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddTelegramBot(builder.Configuration)
///     .AckCallbacks(CallbackAckMode.Manual)
///     .QueueBy(QueueKeyStrategy.ChatAndUser)
///     .KeepStateFor(TimeSpan.FromHours(6))
///     .UseMiddleware&lt;EnsureUserMiddleware&gt;()
///     .UseStateStorage&lt;MyStateStorage&gt;();
/// </code>
/// </example>
public sealed class TelegramBotBuilder
{
    private readonly List<Type> _builtInMiddleware =
    [
        typeof(UpdateLoggingMiddleware),
        typeof(ErrorMiddleware),
        typeof(BotMessageFilterMiddleware),
        typeof(RateLimitMiddleware)
    ];

    private readonly List<Type> _middleware = [];
    private readonly List<Assembly> _assemblies = [];

    internal TelegramBotBuilder(IServiceCollection services) => Services = services;

    /// <summary>
    /// Gets the service collection, for registering your own services.
    /// </summary>
    public IServiceCollection Services { get; }

    internal IReadOnlyList<Assembly> Assemblies => _assemblies;

    /// <summary>
    /// Controllers of the library, such as the <c>/paysupport</c> answer; actions of yours with the same route replace them.
    /// </summary>
    internal List<Type> BuiltInControllers { get; } = [];

    /// <summary>
    /// Checks that run when the bot starts, after the actions are known. Each one throws when something is missing.
    /// </summary>
    internal List<Action<IServiceProvider, ActionRegistry>> StartupChecks { get; } = [];

    internal bool StarsEnabled { get; set; }

    internal CallbackAckMode CallbackAckMode { get; private set; } = CallbackAckMode.Immediate;

    internal QueueKeyStrategy QueueKeyStrategy { get; private set; } = QueueKeyStrategy.Chat;

    internal TimeSpan StateTimeToLive { get; private set; } = TimeSpan.FromDays(1);

    internal int ParallelUpdatesPerQueue { get; private set; } = 1;

    /// <summary>
    /// Polling unless <c>UseWebhook()</c> from <c>TelegramBotForge.AspNetCore</c> was called.
    /// </summary>
    internal TelegramBotUpdateMode UpdateMode { get; set; } = TelegramBotUpdateMode.Polling;

    internal StarsCatalog StarsCatalog { get; } = new();

    private readonly ConcurrentDictionary<Type, IReadOnlyList<Type>> _systemViewOverrides = new();

    /// <summary>
    /// Finds the classes in your project that redefine a built-in view by deriving from it.
    /// </summary>
    internal IReadOnlyList<Type> FindOverrides(Type systemView) => _systemViewOverrides.GetOrAdd(systemView, view =>
        _assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsSubclassOf(view))
            .ToList());

    /// <summary>
    /// Creates a built-in view: your class if you redefined it, otherwise the library's.
    /// </summary>
    internal SystemView CreateSystemView(IServiceProvider provider, Type systemView)
    {
        var overrides = FindOverrides(systemView);
        var type = overrides.Count == 1 ? overrides[0] : systemView;

        var view = (SystemView)ActivatorUtilities.CreateInstance(provider, type);
        view.Configure(provider.GetRequiredService<IOptions<TelegramBotConfiguration>>().Value);
        return view;
    }

    /// <summary>
    /// Sets when button presses are answered. The default is <see cref="Configurations.CallbackAckMode.Immediate"/>:
    /// answered on arrival, and <c>Answer(...)</c> in actions is ignored. Use <see cref="Configurations.CallbackAckMode.Manual"/>
    /// when your actions show notifications or alerts with <c>Answer(...)</c>.
    /// </summary>
    public TelegramBotBuilder AckCallbacks(CallbackAckMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown callback mode.");

        CallbackAckMode = mode;
        return this;
    }

    /// <summary>
    /// Sets how updates are grouped into ordered queues. The default is <see cref="Configurations.QueueKeyStrategy.Chat"/>,
    /// one queue per chat. Every strategy keeps the steps of one conversation in order.
    /// </summary>
    public TelegramBotBuilder QueueBy(QueueKeyStrategy strategy)
    {
        if (!Enum.IsDefined(strategy))
            throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown queue strategy.");

        QueueKeyStrategy = strategy;
        return this;
    }

    /// <summary>
    /// Sets how many updates of one queue are processed at the same time. Keep the default of <c>1</c> unless your bot
    /// has no forms and the order of messages does not matter: with more, updates of one conversation can run out of order
    /// and overwrite each other's conversation state.
    /// </summary>
    public TelegramBotBuilder ParallelPerQueue(int updatesAtOnce)
    {
        if (updatesAtOnce < 1)
            throw new ArgumentOutOfRangeException(nameof(updatesAtOnce), updatesAtOnce, "At least one update per queue must be processed at a time.");

        ParallelUpdatesPerQueue = updatesAtOnce;
        return this;
    }

    /// <summary>
    /// Sets how long an unfinished conversation state (a form the user started) is kept after the user's last step.
    /// After that the user is back to no state. The default is one day.
    /// </summary>
    public TelegramBotBuilder KeepStateFor(TimeSpan timeToLive)
    {
        if (timeToLive <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeToLive), timeToLive, "The state lifetime must be greater than zero.");

        StateTimeToLive = timeToLive;
        return this;
    }

    /// <summary>
    /// Adds a middleware. Middleware runs in the order it is added, after the built-in ones
    /// (logging, errors, bot filter, rate limit) and before the conversation state is loaded.
    /// </summary>
    /// <param name="lifetime">The lifetime in the DI container. Scoped by default, so it can use scoped services.</param>
    public TelegramBotBuilder UseMiddleware<TMiddleware>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TMiddleware : class, IBotMiddleware
    {
        Services.TryAdd(new ServiceDescriptor(typeof(TMiddleware), typeof(TMiddleware), lifetime));
        if (!_middleware.Contains(typeof(TMiddleware)) && !_builtInMiddleware.Contains(typeof(TMiddleware)))
            _middleware.Add(typeof(TMiddleware));
        return this;
    }

    /// <summary>
    /// Removes a middleware, for example the built-in <see cref="BotMessageFilterMiddleware"/>.
    /// </summary>
    public TelegramBotBuilder RemoveMiddleware<TMiddleware>() where TMiddleware : IBotMiddleware
    {
        _builtInMiddleware.Remove(typeof(TMiddleware));
        _middleware.Remove(typeof(TMiddleware));
        return this;
    }

    /// <summary>
    /// Puts <typeparamref name="TNew"/> in the place of <typeparamref name="TOld"/>, for example your own error middleware
    /// derived from <see cref="ErrorMiddleware"/>.
    /// </summary>
    /// <param name="lifetime">The lifetime of the new middleware. Scoped by default.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="TOld"/> is not in the pipeline.</exception>
    public TelegramBotBuilder ReplaceMiddleware<TOld, TNew>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TOld : IBotMiddleware
        where TNew : class, IBotMiddleware
    {
        var list = _builtInMiddleware.Contains(typeof(TOld)) ? _builtInMiddleware
            : _middleware.Contains(typeof(TOld)) ? _middleware
            : throw new InvalidOperationException($"{typeof(TOld).Name} is not in the pipeline, so it cannot be replaced.");

        list[list.IndexOf(typeof(TOld))] = typeof(TNew);
        Services.TryAdd(new ServiceDescriptor(typeof(TNew), typeof(TNew), lifetime));
        return this;
    }

    /// <summary>
    /// Runs a filter around every action, before the filters of controllers and actions.
    /// <c>[PreCheckout]</c> and <c>[Paid]</c> actions are skipped, so a filter can never lose a payment.
    /// </summary>
    /// <param name="lifetime">The lifetime in the DI container. Scoped by default, so it can use scoped services.</param>
    public TelegramBotBuilder AddFilter<TFilter>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TFilter : class, Controllers.IBotActionFilter
    {
        Services.TryAdd(new ServiceDescriptor(typeof(TFilter), typeof(TFilter), lifetime));
        if (!_filters.Contains(typeof(TFilter)))
            _filters.Add(typeof(TFilter));
        return this;
    }

    internal IReadOnlyList<Type> GlobalFilters => _filters;

    private readonly List<Type> _filters = [];

    /// <summary>
    /// Puts your commands in Telegram's <c>/</c> menu when the bot starts, from the <c>[Command]</c> attributes
    /// that have a <see cref="Controllers.CommandAttribute.Description"/>. Commands limited with <c>[ChatType]</c> to private
    /// chats or to groups appear only in that menu. This replaces the menu set in @BotFather.
    /// </summary>
    /// <remarks>
    /// With <see cref="UseLocalization"/>, a description can be a key that is translated for every
    /// language in <see cref="Localization.BotLocalizationOptions.SupportedCultures"/>.
    /// </remarks>
    public TelegramBotBuilder RegisterCommands()
    {
        RegistersCommands = true;
        return this;
    }

    internal bool RegistersCommands { get; private set; }

    /// <summary>
    /// Sets the culture of every update to the user's language, so <c>IStringLocalizer</c>, <c>.resx</c> resources and
    /// formatting answer in it. By default the language is the one of the user's Telegram app; register your own
    /// <see cref="Localization.IBotCultureResolver"/> to use a language stored by your application.
    /// </summary>
    /// <example>
    /// <code>
    /// .UseLocalization(options =>
    /// {
    ///     options.DefaultCulture = "en";
    ///     options.SupportedCultures = ["en", "ru", "ka"];
    /// })
    /// .UseCultureResolver&lt;UserLanguageResolver&gt;(); // optional: the language the user picked in your bot
    /// </code>
    /// </example>
    public TelegramBotBuilder UseLocalization(Action<Localization.BotLocalizationOptions>? configure = null)
    {
        var options = new Localization.BotLocalizationOptions();
        configure?.Invoke(options);
        options.Validate();

        Localization = options;
        Services.Replace(ServiceDescriptor.Singleton(options));
        Services.TryAddScoped<Localization.IBotCultureResolver, Localization.TelegramLanguageCultureResolver>();
        Services.TryAddSingleton<Localization.LocalizationMiddleware>();
        return this;
    }

    /// <summary>
    /// Replaces how the user's language is chosen, for example with the language the user picked in your bot and you stored.
    /// Calls <see cref="UseLocalization"/> with its defaults if it was not called.
    /// </summary>
    /// <param name="lifetime">The lifetime in the DI container. Scoped by default, so it can use your database.</param>
    public TelegramBotBuilder UseCultureResolver<TResolver>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TResolver : class, Localization.IBotCultureResolver
    {
        if (Localization is null)
            UseLocalization();

        Services.Replace(new ServiceDescriptor(typeof(Localization.IBotCultureResolver), typeof(TResolver), lifetime));
        return this;
    }

    internal Localization.BotLocalizationOptions? Localization { get; private set; }

    /// <summary>
    /// Takes a lock around every update of a conversation, for bots that run as several instances behind a load balancer.
    /// See <see cref="Hosting.IConversationLock"/>.
    /// </summary>
    public TelegramBotBuilder UseConversationLock<TLock>() where TLock : class, Hosting.IConversationLock
    {
        Services.Replace(ServiceDescriptor.Singleton<Hosting.IConversationLock, TLock>());
        return this;
    }

    /// <summary>
    /// Replaces where conversation states are kept. The default keeps them in memory.
    /// </summary>
    public TelegramBotBuilder UseStateStorage<TStorage>() where TStorage : class, IStateStorage
    {
        Services.Replace(ServiceDescriptor.Singleton<IStateStorage, TStorage>());
        return this;
    }

    /// <summary>
    /// Sets defaults for all views: button labels and pagination. A view can override them in <c>ConfigurePagination</c>,
    /// and a single call can override both. They apply to every bot in the process.
    /// </summary>
    /// <example>
    /// <code>
    /// .ConfigureViews(views =>
    /// {
    ///     views.BackText = "⬅ Back";
    ///     views.Pagination(p => p.Previous("◀").Next("▶").AtEdges(PaginationEdge.Placeholder));
    /// })
    /// </code>
    /// </example>
    public TelegramBotBuilder ConfigureViews(Action<ViewDefaults> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(ViewDefaults.Current);
        return this;
    }

    /// <summary>
    /// Adds the controllers and views of another assembly. The assembly that calls <c>AddTelegramBot</c> is always included.
    /// </summary>
    public TelegramBotBuilder AddControllersFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (_assemblies.Contains(assembly))
            return this;

        _assemblies.Add(assembly);
        ViewRegistration.AddViews(Services, assembly);
        return this;
    }

    /// <summary>
    /// The pipeline: logging, the user's language if enabled, the other built-in middleware, then yours, then Stars payments if enabled, then the state, which the router needs.
    /// </summary>
    internal IReadOnlyList<Type> GetMiddlewareTypes()
    {
        var builtIn = _builtInMiddleware.ToList();
        if (Localization is not null)
        {
            // Right after logging: errors, the rate limit message and your middleware already speak the user's language.
            var logging = builtIn.IndexOf(typeof(UpdateLoggingMiddleware));
            builtIn.Insert(logging + 1, typeof(Localization.LocalizationMiddleware));
        }

        return [.. builtIn, .. _middleware, .. (StarsEnabled ? new[] { typeof(StarsMiddleware) } : Array.Empty<Type>()), typeof(StateMiddleware)];
    }

    /// <summary>
    /// Runs the startup checks. Payment routes without <c>AddStars</c> would never match, so they are an error too.
    /// </summary>
    internal void RunStartupChecks(IServiceProvider provider, ActionRegistry registry)
    {
        if (!StarsEnabled)
        {
            var paymentActions = registry.Actions
                .Where(action => action.Routes.Any(route => route is PaidAttribute or PreCheckoutAttribute))
                .Select(action => action.DisplayName)
                .ToList();

            if (paymentActions.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{string.Join(", ", paymentActions)} handle Stars payments, but Stars are not enabled. Call AddStars() at startup.");
            }
        }

        var conflicts = SystemView.All
            .Select(view => (View: view, Overrides: FindOverrides(view)))
            .Where(entry => entry.Overrides.Count > 1)
            .Select(entry => $"{string.Join(" and ", entry.Overrides.Select(type => type.Name))} both redefine {entry.View.Name}. Keep one.")
            .ToList();

        if (conflicts.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, conflicts));

        foreach (var check in StartupChecks)
            check(provider, registry);
    }
}
