using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using TelegramBotForge.Configurations;
using TelegramBotForge.Hosting;
using TelegramBotForge.Routing;

namespace TelegramBotForge.Testing;

/// <summary>
/// Runs your bot in a test, without Telegram: the same controllers, views, middleware, filters, state and payments,
/// with a <see cref="FakeTelegramBotClient"/> instead of the real client. Updates are processed one at a time,
/// and each call returns when the bot has answered.
/// </summary>
/// <example>
/// <code>
/// await using var bot = BotTestHost.Create&lt;StartController&gt;(services => services.AddSingleton&lt;IShop, FakeShop&gt;());
/// var user = bot.PrivateChat();
///
/// var reply = await user.SendText("/start");
/// Assert.Equal("Hello!", reply.Text);
///
/// await user.PressButton("🛒 Catalog");
/// Assert.Contains("Page 1", user.LastMessage!.Text);
/// </code>
/// </example>
public sealed class BotTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly UpdateDispatcher _dispatcher;
    private readonly TelegramBotBuilder _builder;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private int _nextUpdateId;
    private long _nextUserId = 100_000;

    private BotTestHost(ServiceProvider services, TelegramBotBuilder builder)
    {
        _services = services;
        _builder = builder;
        Telegram = (FakeTelegramBotClient)services.GetRequiredService<ITelegramBotClient>();

        // What starting the host would check: the configuration and the controllers.
        _ = services.GetRequiredService<IOptions<TelegramBotConfiguration>>().Value;
        _ = services.GetRequiredService<ActionRegistry>();

        services.GetRequiredService<BotIdentity>().Username = Telegram.Me.Username;
        _dispatcher = services.GetRequiredService<UpdateDispatcher>();
    }

    /// <summary>
    /// Gets the fake Telegram client: every request the bot made, and the messages in each chat.
    /// </summary>
    public FakeTelegramBotClient Telegram { get; }

    /// <summary>
    /// Gets the bot's services, for example to look at your fake repositories.
    /// </summary>
    public IServiceProvider Services => _services;

    /// <summary>
    /// Creates a host for the bot whose controllers and views are in the assembly of <typeparamref name="TBotAssembly"/>.
    /// </summary>
    /// <typeparam name="TBotAssembly">Any type of your bot project, such as one of its controllers.</typeparam>
    /// <param name="configureServices">Registers your services, or fakes of them.</param>
    /// <param name="configureBot">Configures the bot like after <c>AddTelegramBot</c> in your <c>Program.cs</c>: <c>AddStars</c>, middleware, filters…</param>
    /// <param name="configuration">Settings of the <c>TelegramBotConfiguration</c> section, such as <c>["RateLimit:Enabled"] = "true"</c>.
    /// The token and <c>Stars:PaySupportMessage</c> are set for you, the rate limit and the library's logs are off unless you set them.</param>
    public static BotTestHost Create<TBotAssembly>(
        Action<IServiceCollection>? configureServices = null,
        Action<TelegramBotBuilder>? configureBot = null,
        IDictionary<string, string?>? configuration = null) =>
        Create(typeof(TBotAssembly).Assembly, configureServices, configureBot, configuration);

    /// <summary>
    /// Creates a host for the bot whose controllers and views are in <paramref name="botAssembly"/>.
    /// </summary>
    /// <param name="botAssembly">The assembly of your bot.</param>
    /// <param name="configureServices">Registers your services, or fakes of them.</param>
    /// <param name="configureBot">Configures the bot like after <c>AddTelegramBot</c>.</param>
    /// <param name="configuration">Settings of the <c>TelegramBotConfiguration</c> section.</param>
    public static BotTestHost Create(
        Assembly botAssembly,
        Action<IServiceCollection>? configureServices = null,
        Action<TelegramBotBuilder>? configureBot = null,
        IDictionary<string, string?>? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(botAssembly);

        const string section = TelegramBotConfiguration.SectionName;
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{section}:Token"] = "7000000001:TEST-TOKEN",
            [$"{section}:RateLimit:Enabled"] = "false",
            [$"{section}:Logging:Enabled"] = "false",

            // Required at startup by bots that sell with Stars; a test rarely cares about its text.
            [$"{section}:Stars:PaySupportMessage"] = "Payment support (test)"
        };
        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
            settings[key.StartsWith(section + ":", StringComparison.OrdinalIgnoreCase) ? key : $"{section}:{key}"] = value;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));

        var builder = services.AddTelegramBot(config).AddControllersFromAssembly(botAssembly);
        configureBot?.Invoke(builder);
        configureServices?.Invoke(services);

        services.Replace(ServiceDescriptor.Singleton<ITelegramBotClient>(new FakeTelegramBotClient()));

        return new BotTestHost(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), builder);
    }

    /// <summary>
    /// Creates a user in a private chat with the bot. The chat id is the user id, like in Telegram.
    /// </summary>
    /// <param name="userId">The user id, or <c>null</c> for a new one.</param>
    /// <param name="languageCode">The language of the user's Telegram app.</param>
    /// <param name="firstName">The user's first name.</param>
    /// <param name="username">The user's username, without <c>@</c>.</param>
    public TestChat PrivateChat(long? userId = null, string? languageCode = "en", string firstName = "Test", string? username = null)
    {
        var user = NewUser(userId, languageCode, firstName, username);
        return new TestChat(this, new Chat { Id = user.Id, Type = global::Telegram.Bot.Types.Enums.ChatType.Private, FirstName = firstName }, user);
    }

    /// <summary>
    /// Creates a user in a group (a supergroup) with the bot.
    /// </summary>
    /// <param name="chatId">The group id; negative, like in Telegram.</param>
    /// <param name="userId">The user id, or <c>null</c> for a new one.</param>
    /// <param name="languageCode">The language of the user's Telegram app.</param>
    public TestChat GroupChat(long chatId = -1001000000001, long? userId = null, string? languageCode = "en")
    {
        if (chatId >= 0)
            throw new ArgumentOutOfRangeException(nameof(chatId), chatId, "Group ids are negative.");

        var user = NewUser(userId, languageCode, "Test", null);
        return new TestChat(this, new Chat { Id = chatId, Type = global::Telegram.Bot.Types.Enums.ChatType.Supergroup, Title = "Test group" }, user);
    }

    /// <summary>
    /// Processes any update, for update types <see cref="TestChat"/> has no method for. Its id is set if it has none.
    /// </summary>
    public Task<UpdateResult> SendUpdate(Update update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return SendUpdate(update, null, ct);
    }

    /// <summary>
    /// Processes the items of an album like the queues do: as one update when the bot has an <c>[Album]</c> action,
    /// otherwise one by one.
    /// </summary>
    internal async Task<UpdateResult> SendAlbum(IReadOnlyList<Message> items, CancellationToken ct)
    {
        if (_services.GetRequiredService<ActionRegistry>().HasAlbumActions && _builder.ParallelUpdatesPerQueue == 1)
            return await SendUpdate(new Update { Message = items[0] }, items, ct);

        var requests = new List<global::Telegram.Bot.Requests.Abstractions.IRequest>();
        foreach (var item in items)
            requests.AddRange((await SendUpdate(new Update { Message = item }, null, ct)).Requests);
        return new UpdateResult(requests);
    }

    private async Task<UpdateResult> SendUpdate(Update update, IReadOnlyList<Message>? album, CancellationToken ct)
    {

        await _oneAtATime.WaitAsync(ct);
        try
        {
            if (update.Id == 0)
                update.Id = Interlocked.Increment(ref _nextUpdateId);

            var before = Telegram.Requests.Count;

            // Like the queues: button presses are answered on arrival unless the bot answers them itself.
            var answered = false;
            if (update.CallbackQuery is { } query && _builder.CallbackAckMode == CallbackAckMode.Immediate)
            {
                await Telegram.AnswerCallbackQuery(query.Id, cancellationToken: ct);
                answered = true;
            }

            await _dispatcher.DispatchAsync(update, answered, ct, album);
            return new UpdateResult(Telegram.Requests.Skip(before).ToList());
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Puts the commands in the <c>/</c> menus, as <c>RegisterCommands()</c> does when the bot starts.
    /// The calls are in <see cref="FakeTelegramBotClient.Requests"/>.
    /// </summary>
    public Task RegisterCommandsAsync(CancellationToken ct = default) =>
        new BotCommandRegistrar(
                Telegram,
                _services.GetRequiredService<ActionRegistry>(),
                _builder.Localization,
                _services.GetRequiredService<ILoggerFactory>().CreateLogger("TelegramBotForge.Commands"))
            .RegisterAsync(ct);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        _oneAtATime.Dispose();
    }

    internal int NextMessageId() => Interlocked.Increment(ref _nextUpdateId) + 1_000_000;

    private User NewUser(long? userId, string? languageCode, string firstName, string? username) => new()
    {
        Id = userId ?? Interlocked.Increment(ref _nextUserId),
        FirstName = firstName,
        Username = username,
        LanguageCode = languageCode
    };
}
