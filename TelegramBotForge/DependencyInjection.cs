using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using TelegramBotForge.Configurations;
using TelegramBotForge.Controllers;
using TelegramBotForge.Hosting;
using TelegramBotForge.Logging;
using TelegramBotForge.Media;
using TelegramBotForge.Middleware;
using TelegramBotForge.Results;
using TelegramBotForge.Routing;
using TelegramBotForge.State;
using TelegramBotForge.Views;

namespace TelegramBotForge;

/// <summary>
/// Registers TelegramBotForge in an <see cref="IServiceCollection"/>.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the bot: binds <see cref="TelegramBotConfiguration"/>, creates the Telegram client,
    /// finds the controllers and views of the calling assembly, and starts receiving updates when the host starts.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration that contains the bot settings.</param>
    /// <param name="sectionName">The configuration section with the settings.</param>
    /// <returns>A builder to configure the pipeline, the state storage and more.</returns>
    /// <example>
    /// <code>
    /// var builder = Host.CreateApplicationBuilder(args);
    /// builder.Services.AddTelegramBot(builder.Configuration);
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static TelegramBotBuilder AddTelegramBot(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = TelegramBotConfiguration.SectionName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(TelegramBotBuilder)))
            throw new InvalidOperationException("AddTelegramBot was already called.");

        var section = configuration.GetSection(sectionName);
        ThrowIfMovedToCode(section, sectionName);

        var builder = new TelegramBotBuilder(services);
        services.AddSingleton(builder);

        services.AddOptions<TelegramBotConfiguration>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<TelegramBotConfiguration>, TelegramBotConfigurationValidator>(
            _ => new TelegramBotConfigurationValidator(builder));
        AddLogging(services, section.GetSection(nameof(TelegramBotConfiguration.Logging)).Get<TelegramBotLoggingOptions>() ?? new());

        services.TryAddSingleton<ITelegramBotClient>(CreateClient);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<BotIdentity>();
        services.TryAddSingleton<IStateStorage, InMemoryStateStorage>();
        services.TryAddSingleton<IBotResultExecutor, TelegramResultExecutor>();
        services.TryAddSingleton<IBotFiles, TelegramBotFiles>();
        services.TryAddSingleton<Broadcasting.IBotBroadcaster, Broadcasting.TelegramBotBroadcaster>();

        services.TryAddSingleton<UpdateLoggingMiddleware>();
        services.TryAddSingleton<ErrorMiddleware>();
        services.TryAddSingleton<BotMessageFilterMiddleware>();
        services.TryAddSingleton<RateLimitMiddleware>();
        services.TryAddSingleton<StateMiddleware>();

        builder.AddControllersFromAssembly(Assembly.GetCallingAssembly());

        // The messages the library shows itself; a class of yours deriving from one replaces it.
        foreach (var systemView in SystemView.All)
            services.AddScoped(systemView, provider => builder.CreateSystemView(provider, systemView));
        builder.BuiltInControllers.Add(typeof(UnknownCommandController));

        // Built when the host starts, after the builder is configured; problems in controllers stop the bot right away.
        services.AddSingleton(provider =>
        {
            var registry = ActionRegistry.Build(builder.Assemblies, builder.BuiltInControllers, builder.GlobalFilters,
                provider.GetService<IServiceProviderIsService>());
            builder.RunStartupChecks(provider, registry);
            return registry;
        });
        services.AddSingleton<BotRouter>();
        services.AddSingleton(provider => new BotPipeline(
            builder.GetMiddlewareTypes(),
            (context, ct) => provider.GetRequiredService<BotRouter>().RouteAsync(context, ct)));

        services.AddSingleton<UpdateDispatcher>();
        services.AddSingleton<UpdateQueues>();
        services.AddSingleton<IBotUpdateQueue>(provider => provider.GetRequiredService<UpdateQueues>());

        // Hosted services stop in reverse order: the receiver stops first, then the queues finish what they accepted.
        services.AddHostedService(provider => provider.GetRequiredService<UpdateQueues>());
        services.AddHostedService<TelegramUpdateReceiver>();

        return builder;
    }

    /// <summary>
    /// Settings that used to be in configuration and are now set in code. Silently ignoring them would change how the bot behaves.
    /// </summary>
    private static readonly (string Key, string Replacement)[] MovedToCode =
    [
        ("CallbackAckMode", ".AckCallbacks(CallbackAckMode.Manual)"),
        ("KeyStrategy", ".QueueBy(QueueKeyStrategy.ChatAndUser)"),
        ("MaxDegreeOfParallelismPerChat", ".ParallelPerQueue(2)"),
        ("ConversationStateTtlMinutes", ".KeepStateFor(TimeSpan.FromHours(12))"),
        ("UpdateMode", ".UseWebhook() from TelegramBotForge.AspNetCore")
    ];

    private static void ThrowIfMovedToCode(IConfigurationSection section, string sectionName)
    {
        var found = MovedToCode.Where(entry => section.GetSection(entry.Key).Exists()).ToList();
        if (found.Count == 0)
            return;

        throw new InvalidOperationException(
            "These settings are set in code now. Remove them from the configuration and call the builder method after AddTelegramBot:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, found.Select(entry => $" - {sectionName}:{entry.Key} → {entry.Replacement}")));
    }

    private static ITelegramBotClient CreateClient(IServiceProvider provider)
    {
        var config = provider.GetRequiredService<IOptions<TelegramBotConfiguration>>().Value;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("TelegramBotForge.TelegramClient");

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(config.TelegramConnectTimeoutSeconds),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(config.TelegramPooledConnectionIdleTimeoutSeconds),
            PooledConnectionLifetime = TimeSpan.FromSeconds(config.TelegramPooledConnectionLifetimeSeconds)
        };

        var baseUrl = string.IsNullOrWhiteSpace(config.BotApiBaseUrl) ? null : config.BotApiBaseUrl;
        logger.LogInformation("Telegram client: server {BotApiServer}, request timeout {TimeoutSeconds}s",
            baseUrl ?? "https://api.telegram.org",
            config.TelegramHttpTimeoutSeconds);

        var httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(config.TelegramHttpTimeoutSeconds)
        };

        var flood = config.FloodControl ?? new TelegramBotFloodControlOptions();
        var clientOptions = new TelegramBotClientOptions(config.Token, baseUrl)
        {
            // Telegram.Bot repeats a request after a 429 when the wait Telegram asks for is at most RetryThreshold seconds.
            RetryCount = flood.RetryCount,
            RetryThreshold = flood.MaxRetryAfterSeconds
        };

        ITelegramBotClient client = new TelegramBotClient(clientOptions, httpClient);
        client = new Telemetry.TelemetryBotClient(client);
        if (!flood.Enabled)
            return client;

        logger.LogInformation(
            "Flood control: {GlobalPerSecond} messages/s overall, {PrivatePerSecond}/s per private chat (bursts of {Burst}), " +
            "{GroupPerMinute}/min per group",
            flood.GlobalPerSecond, flood.PrivateChatPerSecond, flood.PrivateChatBurst, flood.GroupPerMinute);
        return new FloodControlledBotClient(client, flood, provider.GetRequiredService<ILoggerFactory>().CreateLogger("TelegramBotForge.FloodControl"));
    }

    /// <summary>
    /// Applies <see cref="TelegramBotLoggingOptions"/>: turns TelegramBotForge logs off or sets their level,
    /// and switches the console to <see cref="TelegramBotConsoleFormatter"/> when <see cref="TelegramBotLoggingOptions.PrettyConsole"/> is on.
    /// </summary>
    private static void AddLogging(IServiceCollection services, TelegramBotLoggingOptions options)
    {
        services.AddLogging(logging =>
        {
            if (!options.Enabled)
            {
                logging.AddFilter("TelegramBotForge", LogLevel.None);
                return;
            }

            logging.AddFilter("TelegramBotForge", options.MinimumLevel);

            if (!options.PrettyConsole)
                return;

            logging.AddConsole(console => console.FormatterName = TelegramBotConsoleFormatter.FormatterName);
            logging.AddConsoleFormatter<TelegramBotConsoleFormatter, TelegramBotConsoleFormatterOptions>(formatter =>
                formatter.UseColors = options.UseColors);
        });
    }
}
