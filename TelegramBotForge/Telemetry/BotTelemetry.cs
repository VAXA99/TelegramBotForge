using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace TelegramBotForge.Telemetry;

/// <summary>
/// The names TelegramBotForge publishes metrics and traces under, for OpenTelemetry or any other listener.
/// Nothing is recorded until something listens, so it costs nothing when unused.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithMetrics(metrics => metrics.AddMeter(BotTelemetry.MeterName).AddPrometheusExporter())
///     .WithTracing(tracing => tracing.AddSource(BotTelemetry.ActivitySourceName).AddOtlpExporter());
/// </code>
/// </example>
/// <remarks>
/// Metrics:
/// <list type="bullet">
/// <item><c>telegrambot.updates</c> (counter): processed updates, by <c>update.type</c> and <c>outcome</c> (handled, ignored, failed).</item>
/// <item><c>telegrambot.update.duration</c> (histogram, s): time from the start of processing to the sent answer, by <c>update.type</c>, <c>action</c> and <c>outcome</c>.</item>
/// <item><c>telegrambot.api.requests</c> (counter): calls to the Bot API, by <c>method</c> and <c>outcome</c> (ok, or the error code).</item>
/// <item><c>telegrambot.api.duration</c> (histogram, s): duration of Bot API calls, by <c>method</c>.</item>
/// <item><c>telegrambot.floodcontrol.wait</c> (histogram, s): how long messages waited for Telegram's limits.</item>
/// <item><c>telegrambot.broadcast.messages</c> (counter): broadcast messages, by <c>outcome</c> (sent, blocked, failed).</item>
/// <item><c>telegrambot.queue.length</c> (gauge): updates waiting in the queues.</item>
/// </list>
/// Traces: one <c>telegram.update</c> activity per update, with a child activity for every Bot API call.
/// </remarks>
public static class BotTelemetry
{
    /// <summary>
    /// The name of the <see cref="Meter"/>: <c>TelegramBotForge</c>.
    /// </summary>
    public const string MeterName = "TelegramBotForge";

    /// <summary>
    /// The name of the <see cref="ActivitySource"/>: <c>TelegramBotForge</c>.
    /// </summary>
    public const string ActivitySourceName = "TelegramBotForge";

    private static readonly string? Version = typeof(BotTelemetry).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    internal static readonly Meter Meter = new(MeterName, Version);

    internal static readonly Counter<long> Updates = Meter.CreateCounter<long>(
        "telegrambot.updates", "{update}", "Processed updates.");

    internal static readonly Histogram<double> UpdateDuration = Meter.CreateHistogram<double>(
        "telegrambot.update.duration", "s", "Time to process an update and send its answer.");

    internal static readonly Counter<long> ApiRequests = Meter.CreateCounter<long>(
        "telegrambot.api.requests", "{request}", "Calls to the Telegram Bot API.");

    internal static readonly Histogram<double> ApiDuration = Meter.CreateHistogram<double>(
        "telegrambot.api.duration", "s", "Duration of Telegram Bot API calls.");

    internal static readonly Histogram<double> FloodControlWait = Meter.CreateHistogram<double>(
        "telegrambot.floodcontrol.wait", "s", "Time outgoing messages waited for Telegram's limits.");

    internal static readonly Counter<long> BroadcastMessages = Meter.CreateCounter<long>(
        "telegrambot.broadcast.messages", "{message}", "Messages sent by broadcasts.");

    private static Func<long>? _queueLength;

    static BotTelemetry()
    {
        Meter.CreateObservableGauge("telegrambot.queue.length", () => _queueLength?.Invoke() ?? 0, "{update}",
            "Updates waiting in the queues.");
    }

    internal static void ObserveQueueLength(Func<long> length) => _queueLength = length;
}
