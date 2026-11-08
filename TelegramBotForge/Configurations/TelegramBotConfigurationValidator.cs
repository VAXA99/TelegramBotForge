using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace TelegramBotForge.Configurations;

/// <summary>
/// Validates <see cref="TelegramBotConfiguration"/> at application startup and reports every invalid value at once.
/// </summary>
public sealed class TelegramBotConfigurationValidator : IValidateOptions<TelegramBotConfiguration>
{
    private readonly TelegramBotBuilder _bot;

    internal TelegramBotConfigurationValidator(TelegramBotBuilder bot) => _bot = bot;

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, TelegramBotConfiguration options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Token))
            errors.Add($"{nameof(options.Token)} is required. Get one from @BotFather.");

        RequirePositive(errors, nameof(options.MaxGlobalConcurrency), options.MaxGlobalConcurrency);
        RequirePositive(errors, nameof(options.PerChatQueueCapacity), options.PerChatQueueCapacity);
        RequirePositive(errors, nameof(options.IdleChatTtlSeconds), options.IdleChatTtlSeconds);
        if (options.AlbumWaitMilliseconds is < 50 or > 5000)
            errors.Add($"{nameof(options.AlbumWaitMilliseconds)} must be between 50 and 5000, but was {options.AlbumWaitMilliseconds}.");
        RequirePositive(errors, nameof(options.TelegramHttpTimeoutSeconds), options.TelegramHttpTimeoutSeconds);
        RequirePositive(errors, nameof(options.TelegramConnectTimeoutSeconds), options.TelegramConnectTimeoutSeconds);
        RequirePositive(errors, nameof(options.TelegramPooledConnectionIdleTimeoutSeconds), options.TelegramPooledConnectionIdleTimeoutSeconds);
        RequirePositive(errors, nameof(options.TelegramPooledConnectionLifetimeSeconds), options.TelegramPooledConnectionLifetimeSeconds);
        RequirePositive(errors, nameof(options.PollingNetworkErrorBaseDelaySeconds), options.PollingNetworkErrorBaseDelaySeconds);
        RequirePositive(errors, nameof(options.PollingNetworkErrorMaxDelaySeconds), options.PollingNetworkErrorMaxDelaySeconds);
        RequirePositive(errors, nameof(options.PollingNetworkErrorErrorThreshold), options.PollingNetworkErrorErrorThreshold);

        if (options.PollingNetworkErrorMaxDelaySeconds < options.PollingNetworkErrorBaseDelaySeconds)
        {
            errors.Add($"{nameof(options.PollingNetworkErrorMaxDelaySeconds)} ({options.PollingNetworkErrorMaxDelaySeconds}) " +
                       $"must be greater than or equal to {nameof(options.PollingNetworkErrorBaseDelaySeconds)} ({options.PollingNetworkErrorBaseDelaySeconds}).");
        }

        RequireAbsoluteUrl(errors, nameof(options.BotApiBaseUrl), options.BotApiBaseUrl);

        if (_bot.UpdateMode == TelegramBotUpdateMode.Webhook)
        {
            var webhook = options.Webhook ?? new TelegramBotWebhookOptions();
            if (!Uri.TryCreate(webhook.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                errors.Add($"{nameof(options.Webhook)}:{nameof(webhook.Url)} must be an absolute https:// URL in webhook mode, but was \"{webhook.Url}\".");
            if (webhook.SecretToken is { } secret && !Regex.IsMatch(secret, "^[A-Za-z0-9_-]{1,256}$"))
                errors.Add($"{nameof(options.Webhook)}:{nameof(webhook.SecretToken)} may only contain letters, digits, _ and -, up to 256 characters.");
            if (webhook.MaxConnections is < 1 or > 100)
                errors.Add($"{nameof(options.Webhook)}:{nameof(webhook.MaxConnections)} must be between 1 and 100, but was {webhook.MaxConnections}.");
        }

        if (options.RateLimit is { Enabled: true } rateLimit)
        {
            RequirePositive(errors, $"{nameof(options.RateLimit)}:{nameof(rateLimit.MaxUpdates)}", rateLimit.MaxUpdates);
            RequirePositive(errors, $"{nameof(options.RateLimit)}:{nameof(rateLimit.PerSeconds)}", rateLimit.PerSeconds);
        }

        if (options.FloodControl is { } flood)
        {
            if (flood.Enabled)
            {
                RequirePositive(errors, $"{nameof(options.FloodControl)}:{nameof(flood.GlobalPerSecond)}", flood.GlobalPerSecond);
                RequirePositive(errors, $"{nameof(options.FloodControl)}:{nameof(flood.PrivateChatPerSecond)}", flood.PrivateChatPerSecond);
                RequirePositive(errors, $"{nameof(options.FloodControl)}:{nameof(flood.PrivateChatBurst)}", flood.PrivateChatBurst);
                RequirePositive(errors, $"{nameof(options.FloodControl)}:{nameof(flood.GroupPerMinute)}", flood.GroupPerMinute);
            }

            if (flood.RetryCount < 0)
                errors.Add($"{nameof(options.FloodControl)}:{nameof(flood.RetryCount)} must be 0 or greater, but was {flood.RetryCount}.");
            if (flood.MaxRetryAfterSeconds < 0)
                errors.Add($"{nameof(options.FloodControl)}:{nameof(flood.MaxRetryAfterSeconds)} must be 0 or greater, but was {flood.MaxRetryAfterSeconds}.");
        }

        var stars = options.Stars ?? new TelegramBotStarsOptions();
        if (stars.PreCheckoutTimeoutSeconds is < 1 or > 9)
            errors.Add($"{nameof(options.Stars)}:{nameof(stars.PreCheckoutTimeoutSeconds)} must be between 1 and 9 (Telegram cancels the payment after 10 seconds), but was {stars.PreCheckoutTimeoutSeconds}.");
        if (string.IsNullOrWhiteSpace(stars.DeclineMessage))
            errors.Add($"{nameof(options.Stars)}:{nameof(stars.DeclineMessage)} must not be empty: Telegram shows it when an order is declined.");

        var logging = options.Logging ?? new TelegramBotLoggingOptions();
        RequireDefined(errors, $"{nameof(options.Logging)}:{nameof(logging.MinimumLevel)}", logging.MinimumLevel);
        if (logging.SlowUpdateWarningMs < 0)
            errors.Add($"{nameof(options.Logging)}:{nameof(logging.SlowUpdateWarningMs)} must be 0 or greater, but was {logging.SlowUpdateWarningMs}.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors.Select(error => $"{TelegramBotConfiguration.SectionName}: {error}"));
    }

    private static void RequirePositive(List<string> errors, string name, int value)
    {
        if (value <= 0)
            errors.Add($"{name} must be greater than 0, but was {value}.");
    }

    private static void RequireDefined<TEnum>(List<string> errors, string name, TEnum value) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            errors.Add($"{name} has an unknown value {value}. Allowed values: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    private static void RequireAbsoluteUrl(List<string> errors, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !Uri.TryCreate(value, UriKind.Absolute, out _))
            errors.Add($"{name} must be an absolute URL, but was \"{value}\".");
    }
}
