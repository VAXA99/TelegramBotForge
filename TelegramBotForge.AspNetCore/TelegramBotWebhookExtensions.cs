using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using TelegramBotForge.Configurations;
using TelegramBotForge.Hosting;

namespace TelegramBotForge.AspNetCore;

/// <summary>
/// Webhook mode for ASP.NET Core: Telegram sends each update to an endpoint of your application instead of the bot polling for it.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddTelegramBot(builder.Configuration).UseWebhook();
/// var app = builder.Build();
/// app.MapTelegramBotWebhook();
/// app.Run();
/// </code>
/// With configuration:
/// <code>
/// "TelegramBotConfiguration": {
///   "Webhook": { "Url": "https://bot.example.com/telegram/webhook", "SecretToken": "a-long-random-string" }
/// }
/// </code>
/// </example>
public static class TelegramBotWebhookExtensions
{
    /// <summary>
    /// The header in which Telegram sends <see cref="TelegramBotWebhookOptions.SecretToken"/>.
    /// </summary>
    public const string SecretTokenHeader = "X-Telegram-Bot-Api-Secret-Token";

    private const string DefaultPath = "/telegram/webhook";

    /// <summary>
    /// Switches the bot to webhook mode: long polling is not started, and the webhook from
    /// <see cref="TelegramBotConfiguration.Webhook"/> is registered with Telegram at startup.
    /// Map the endpoint with <see cref="MapTelegramBotWebhook"/>.
    /// </summary>
    /// <param name="builder">The bot builder.</param>
    /// <returns>The same builder, so calls can be chained.</returns>
    public static TelegramBotBuilder UseWebhook(this TelegramBotBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UpdateMode = TelegramBotUpdateMode.Webhook;
        return builder;
    }

    /// <summary>
    /// Maps the POST endpoint that receives updates from Telegram.
    /// It checks the secret token header, reads the update and puts it into the bot's queue.
    /// </summary>
    /// <param name="endpoints">The application.</param>
    /// <param name="pattern">The route. By default the path of <see cref="TelegramBotWebhookOptions.Url"/>, or <c>/telegram/webhook</c>.</param>
    /// <returns>A builder to customize the endpoint, for example with rate limiting.</returns>
    public static IEndpointConventionBuilder MapTelegramBotWebhook(this IEndpointRouteBuilder endpoints, string? pattern = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (pattern is null)
        {
            var url = endpoints.ServiceProvider.GetService<IOptions<TelegramBotConfiguration>>()?.Value.Webhook?.Url;
            pattern = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.AbsolutePath != "/"
                ? uri.AbsolutePath
                : DefaultPath;
        }

        return endpoints.MapPost(pattern, HandleAsync).WithDisplayName("Telegram webhook");
    }

    /// <summary>
    /// Handles one webhook request. Always answers 200 for a valid update, even if processing fails later,
    /// because Telegram would otherwise resend the same update again and again.
    /// </summary>
    internal static async Task HandleAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var config = services.GetRequiredService<IOptions<TelegramBotConfiguration>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("TelegramBotForge.Webhook");

        if (!HasValidSecret(http.Request, config.Webhook?.SecretToken))
        {
            logger.LogWarning("Rejected a webhook request from {RemoteIp}: wrong or missing secret token",
                http.Connection.RemoteIpAddress);
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        Update? update;
        try
        {
            update = await JsonSerializer.DeserializeAsync<Update>(http.Request.Body, JsonBotAPI.Options, http.RequestAborted);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Rejected a webhook request: the body is not a Telegram update");
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (update is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Queued, not processed here: Telegram waits for this response.
        try
        {
            await services.GetRequiredService<IBotUpdateQueue>().EnqueueAsync(update, http.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not queue webhook update {UpdateId}", update.Id);
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
    }

    private static bool HasValidSecret(HttpRequest request, string? expected)
    {
        if (string.IsNullOrEmpty(expected))
            return true;

        var actual = request.Headers[SecretTokenHeader].ToString();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
    }
}
