using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using TelegramBotForge.Configurations;
using TelegramBotForge.Routing;

namespace TelegramBotForge.Hosting;

/// <summary>
/// Connects the bot to Telegram. In polling mode it asks Telegram for updates and queues them;
/// in webhook mode it registers the webhook, and the ASP.NET Core endpoint queues the updates.
/// </summary>
internal sealed class TelegramUpdateReceiver : BackgroundService
{
    private readonly ITelegramBotClient _bot;
    private readonly IBotUpdateQueue _queue;
    private readonly TelegramBotConfiguration _config;
    private readonly TelegramBotUpdateMode _mode;
    private readonly ILogger<TelegramUpdateReceiver> _logger;
    private readonly Func<BotCommandRegistrar>? _commands;
    private readonly BotIdentity? _identity;
    private int _networkErrorCount;

    public TelegramUpdateReceiver(
        ITelegramBotClient bot,
        IBotUpdateQueue queue,
        IOptions<TelegramBotConfiguration> options,
        TelegramBotBuilder builder,
        ILogger<TelegramUpdateReceiver> logger,
        IServiceProvider? services = null,
        BotIdentity? identity = null)
    {
        _identity = identity;
        if (builder.RegistersCommands && services is not null)
        {
            _commands = () => new BotCommandRegistrar(
                bot,
                services.GetRequiredService<ActionRegistry>(),
                builder.Localization,
                services.GetRequiredService<ILoggerFactory>().CreateLogger("TelegramBotForge.Commands"));
        }

        _bot = bot;
        _queue = queue;
        _config = options.Value;
        _mode = builder.UpdateMode;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ProbeTelegramAsync(stoppingToken);

            if (_commands is not null)
                await _commands().RegisterAsync(stoppingToken);

            if (_mode == TelegramBotUpdateMode.Webhook)
            {
                await SetWebhookAsync(stoppingToken);
                return;
            }

            await DeleteWebhookAsync(stoppingToken);

            _bot.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandleErrorAsync,
                receiverOptions: new ReceiverOptions { AllowedUpdates = _config.AllowedUpdates ?? [] },
                cancellationToken: stoppingToken);

            _logger.LogInformation("Listening for updates (long polling). Update types: {AllowedUpdates}", DescribeAllowedUpdates());
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_mode != TelegramBotUpdateMode.Webhook || !_config.Webhook.DeleteOnShutdown)
            return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _bot.DeleteWebhook(cancellationToken: timeout.Token);
            _logger.LogInformation("Webhook removed on shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the webhook on shutdown");
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        var previousErrors = Interlocked.Exchange(ref _networkErrorCount, 0);
        if (previousErrors > 0)
            _logger.LogInformation("Connection to Telegram restored after {ErrorCount} network errors", previousErrors);

        await _queue.EnqueueAsync(update, ct);
    }

    /// <summary>
    /// Handles long polling errors: network failures wait longer and longer, API errors are logged.
    /// </summary>
    private async Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException && ct.IsCancellationRequested)
            return;

        if (IsNetworkError(exception))
        {
            var errors = Interlocked.Increment(ref _networkErrorCount);
            var delay = GetNetworkErrorDelay(errors);
            var level = errors >= Math.Max(_config.PollingNetworkErrorErrorThreshold, 1) ? LogLevel.Error : LogLevel.Warning;

            _logger.Log(level, exception, "Network error while polling Telegram (#{ErrorCount}). Next try in {DelaySeconds}s",
                errors, (int)delay.TotalSeconds);
            await SafeDelayAsync(delay, ct);
            return;
        }

        Interlocked.Exchange(ref _networkErrorCount, 0);

        switch (exception)
        {
            case ApiRequestException { ErrorCode: 409 }:
                if (_identity is not null)
                    _identity.LastConflict = DateTimeOffset.UtcNow;
                _logger.LogError("Telegram reports a conflict: another process is polling with the same token, or a webhook is set ({Reason})",
                    exception.Message);
                break;
            case ApiRequestException { ErrorCode: 401 }:
                if (_identity is not null)
                    _identity.TokenRejected = true;
                _logger.LogCritical("Telegram rejected the bot token (401 Unauthorized). Check {Section}:Token",
                    TelegramBotConfiguration.SectionName);
                break;
            default:
                _logger.LogError(exception, "Telegram API error while polling");
                break;
        }

        await SafeDelayAsync(TimeSpan.FromSeconds(Math.Max(_config.PollingNetworkErrorBaseDelaySeconds, 1)), ct);
    }

    /// <summary>
    /// Calls <c>GetMe</c> so the connection is logged at startup. A failure does not stop the bot; polling keeps retrying.
    /// </summary>
    private async Task ProbeTelegramAsync(CancellationToken ct)
    {
        try
        {
            var me = await _bot.GetMe(cancellationToken: ct);
            if (_identity is not null)
            {
                _identity.Username = me.Username;
                _identity.TokenRejected = false;
            }
            _logger.LogInformation("Connected to Telegram as @{Username} (id {BotId})", me.Username, me.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is ApiRequestException { ErrorCode: 401 } && _identity is not null)
                _identity.TokenRejected = true;
            _logger.LogError(ex, "Could not reach the Telegram API. Check the token and the network; the bot keeps retrying");
        }
    }

    private async Task SetWebhookAsync(CancellationToken ct)
    {
        var webhook = _config.Webhook;
        try
        {
            await _bot.SetWebhook(
                url: webhook.Url!,
                maxConnections: webhook.MaxConnections,
                allowedUpdates: _config.AllowedUpdates ?? [],
                dropPendingUpdates: webhook.DropPendingUpdates,
                secretToken: string.IsNullOrEmpty(webhook.SecretToken) ? null : webhook.SecretToken,
                cancellationToken: ct);

            _logger.LogInformation("Receiving updates through the webhook {WebhookUrl}. Update types: {AllowedUpdates}",
                webhook.Url, DescribeAllowedUpdates());

            if (string.IsNullOrEmpty(webhook.SecretToken))
                _logger.LogWarning("Webhook:SecretToken is not set, so anyone who knows the URL can send fake updates");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_identity is not null)
                _identity.WebhookFailed = true;
            _logger.LogCritical(ex, "Could not register the webhook {WebhookUrl}; the bot will receive no updates", webhook.Url);
        }
    }

    /// <summary>
    /// Telegram cannot deliver updates through a webhook and long polling at the same time.
    /// </summary>
    private async Task DeleteWebhookAsync(CancellationToken ct)
    {
        try
        {
            await _bot.DeleteWebhook(cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not remove the webhook before polling");
        }
    }

    private TimeSpan GetNetworkErrorDelay(int errors)
    {
        var baseSeconds = Math.Max(_config.PollingNetworkErrorBaseDelaySeconds, 1);
        var maxSeconds = Math.Max(_config.PollingNetworkErrorMaxDelaySeconds, baseSeconds);
        var exponent = Math.Clamp(errors - 1, 0, 5);
        return TimeSpan.FromSeconds(Math.Min(baseSeconds * (1 << exponent), maxSeconds));
    }

    private string DescribeAllowedUpdates() =>
        _config.AllowedUpdates is { Length: > 0 } allowed ? string.Join(", ", allowed) : "Telegram defaults";

    private static bool IsNetworkError(Exception exception)
    {
        if (exception is ApiRequestException)
            return false;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is RequestException or HttpRequestException or IOException or SocketException
                or AuthenticationException or TimeoutException or TaskCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
