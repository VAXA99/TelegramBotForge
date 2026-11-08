using System.Diagnostics;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using TelegramBotForge.Hosting;

namespace TelegramBotForge.Telemetry;

/// <summary>
/// Records every Bot API call: a counter and a duration by method and outcome, and an activity in the current trace.
/// </summary>
internal sealed class TelemetryBotClient : DelegatingBotClient
{
    public TelemetryBotClient(ITelegramBotClient inner)
        : base(inner)
    {
    }

    public override async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        // Long polling waits for updates for a long time; its duration says nothing about Telegram's speed.
        if (request.MethodName == "getUpdates" || !(BotTelemetry.ApiRequests.Enabled || BotTelemetry.ActivitySource.HasListeners()))
            return await Inner.SendRequest(request, cancellationToken);

        using var activity = BotTelemetry.ActivitySource.StartActivity("telegram.api " + request.MethodName, ActivityKind.Client);
        activity?.SetTag("telegram.method", request.MethodName);

        var started = Stopwatch.GetTimestamp();
        var outcome = "ok";
        try
        {
            return await Inner.SendRequest(request, cancellationToken);
        }
        catch (ApiRequestException ex)
        {
            outcome = ex.ErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            outcome = "network_error";
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            var method = new KeyValuePair<string, object?>("method", request.MethodName);
            BotTelemetry.ApiRequests.Add(1, method, new KeyValuePair<string, object?>("outcome", outcome));
            BotTelemetry.ApiDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, method);
            activity?.SetTag("telegram.outcome", outcome);
        }
    }
}
