using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Telegram.Bot;
using Telegram.Bot.Types;
using TelegramBotForge.Configurations;
using TelegramBotForge.Hosting;

namespace TelegramBotForge.AspNetCore;

/// <summary>
/// Reports whether the bot can work: Telegram accepts the token and answers, the webhook is registered and delivers,
/// and no other process takes the updates. Add it with <c>AddHealthChecks().AddTelegramBot()</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Unhealthy</b>: the token was rejected, the webhook could not be registered, or Telegram cannot be reached.</item>
/// <item><b>Degraded</b>: another process polls with the same token (409), or in webhook mode Telegram reported a delivery
/// error in the last 10 minutes or holds too many undelivered updates.</item>
/// </list>
/// Telegram is asked at most every 30 seconds, so frequent probes do not hit its limits.
/// </remarks>
public sealed class TelegramBotHealthCheck : IHealthCheck
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecentError = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ITelegramBotClient _bot;
    private readonly BotIdentity _identity;
    private readonly TelegramBotBuilder _builder;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, HealthCheckResult Result)? _cached;

    internal TelegramBotHealthCheck(ITelegramBotClient bot, BotIdentity identity, TelegramBotBuilder builder, TimeProvider time)
    {
        _bot = bot;
        _identity = identity;
        _builder = builder;
        _time = time;
    }

    /// <summary>
    /// Gets or sets how many updates may wait at Telegram, in webhook mode, before the bot is reported degraded.
    /// </summary>
    public int MaxPendingUpdates { get; set; } = 1000;

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_identity.TokenRejected)
            return HealthCheckResult.Unhealthy("Telegram rejected the bot token (401 Unauthorized).");
        if (_identity.WebhookFailed)
            return HealthCheckResult.Unhealthy("The webhook could not be registered, so no updates arrive.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (_cached is { } cached && now - cached.At < CacheFor)
                return cached.Result;

            var result = await ProbeAsync(now, cancellationToken);
            _cached = (now, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HealthCheckResult> ProbeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var data = new Dictionary<string, object> { ["mode"] = _builder.UpdateMode.ToString() };
        if (_identity.Username is { } username)
            data["bot"] = "@" + username;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            if (_builder.UpdateMode != TelegramBotUpdateMode.Webhook)
            {
                await _bot.GetMe(timeout.Token);
                if (_identity.LastConflict is { } conflict && now - conflict < RecentError)
                    return HealthCheckResult.Degraded("Another process is polling with the same token.", data: data);
                return HealthCheckResult.Healthy("Connected to Telegram (long polling).", data);
            }

            WebhookInfo info = await _bot.GetWebhookInfo(timeout.Token);
            data["pendingUpdates"] = info.PendingUpdateCount;
            if (string.IsNullOrEmpty(info.Url))
                return HealthCheckResult.Unhealthy("No webhook is set at Telegram, so no updates arrive.", data: data);
            if (info.LastErrorDate is { } errorAt && now - new DateTimeOffset(DateTime.SpecifyKind(errorAt, DateTimeKind.Utc)) < RecentError)
                return HealthCheckResult.Degraded($"Telegram could not deliver to the webhook: {info.LastErrorMessage}", data: data);
            if (info.PendingUpdateCount > MaxPendingUpdates)
                return HealthCheckResult.Degraded($"{info.PendingUpdateCount} updates are waiting at Telegram.", data: data);
            return HealthCheckResult.Healthy("Connected to Telegram (webhook).", data);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"Telegram did not answer within {Timeout.TotalSeconds:0} seconds.", data: data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Telegram cannot be reached: " + ex.Message, ex, data);
        }
    }
}

/// <summary>
/// Adds the bot's health check.
/// </summary>
public static class TelegramBotHealthCheckExtensions
{
    /// <summary>
    /// Adds <see cref="TelegramBotHealthCheck"/>. Map it with <c>app.MapHealthChecks("/health")</c>.
    /// </summary>
    /// <param name="builder">The health checks builder.</param>
    /// <param name="name">The name of the check.</param>
    /// <param name="tags">Tags, for example <c>["ready"]</c> to use it only in a readiness probe.</param>
    public static IHealthChecksBuilder AddTelegramBot(this IHealthChecksBuilder builder, string name = "telegram", IEnumerable<string>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddSingleton(provider => new TelegramBotHealthCheck(
            provider.GetRequiredService<ITelegramBotClient>(),
            provider.GetRequiredService<BotIdentity>(),
            provider.GetRequiredService<TelegramBotBuilder>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));
        return builder.Add(new HealthCheckRegistration(name, provider => provider.GetRequiredService<TelegramBotHealthCheck>(), null, tags));
    }
}
