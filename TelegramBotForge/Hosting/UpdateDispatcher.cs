using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using TelegramBotForge.Logging;
using TelegramBotForge.Middleware;
using TelegramBotForge.Results;
using TelegramBotForge.Telemetry;

namespace TelegramBotForge.Hosting;

/// <summary>
/// Processes one update: creates its DI scope and context, runs the pipeline, and sends the result.
/// Called by the queues, so updates of one conversation never reach it at the same time.
/// </summary>
internal sealed class UpdateDispatcher
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ITelegramBotClient _bot;
    private readonly BotPipeline _pipeline;
    private readonly ILogger<UpdateDispatcher> _logger;
    private readonly BotIdentity? _identity;

    public UpdateDispatcher(
        IServiceScopeFactory scopes,
        ITelegramBotClient bot,
        BotPipeline pipeline,
        ILogger<UpdateDispatcher> logger,
        BotIdentity? identity = null)
    {
        _identity = identity;
        _scopes = scopes;
        _bot = bot;
        _pipeline = pipeline;
        _logger = logger;
    }

    /// <param name="update">The update.</param>
    /// <param name="callbackAnswered">The button press was already answered when it was queued.</param>
    /// <param name="ct">Cancelled when the bot stops.</param>
    /// <param name="album">All items of an album, when <paramref name="update"/> is its first one.</param>
    public async Task DispatchAsync(Update update, bool callbackAnswered, CancellationToken ct, IReadOnlyList<Message>? album = null)
    {
        using var activity = BotTelemetry.ActivitySource.StartActivity("telegram.update", ActivityKind.Consumer);
        var started = Stopwatch.GetTimestamp();
        var trace = UpdateTrace.Begin();
        var failed = false;
        try
        {
            failed = !await DispatchCoreAsync(update, callbackAnswered, album, ct);
        }
        finally
        {
            Record(update, trace, failed || trace.Failed, started, activity);
        }
    }

    /// <returns><c>false</c> when the update failed.</returns>
    private async Task<bool> DispatchCoreAsync(Update update, bool callbackAnswered, IReadOnlyList<Message>? album, CancellationToken ct)
    {
        var ok = true;
        await using var scope = _scopes.CreateAsyncScope();
        var context = new BotContext(update, _bot, scope.ServiceProvider, ct) {
            CallbackAnswered = callbackAnswered,
            BotUsername = _identity?.Username,
            AlbumMessages = album
        };
        using var logScope = _logger.BeginScope(new UpdateLogScope(context.ChatId ?? 0, context.UserId ?? 0));

        IBotResult? result;
        try
        {
            result = await _pipeline.InvokeAsync(context, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception ex)
        {
            // Only reached when ErrorMiddleware was removed or rethrows.
            _logger.LogError(ex, "Update {UpdateId} failed", update.Id);
            result = null;
            ok = false;
        }

        try
        {
            if (result is not null)
            {
                var executor = scope.ServiceProvider.GetRequiredService<IBotResultExecutor>();
                await executor.ExecuteAsync(context, result, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return true;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 403)
        {
            _logger.LogWarning("Telegram refused the answer to update {UpdateId}: {Reason}", update.Id, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not send the answer ({Result}) to update {UpdateId}",
                UpdateDescriber.DescribeResult(result), update.Id);
            ok = false;
        }

        // Every pressed button gets an answer, so its loading spinner never hangs.
        if (context.CallbackQuery is not null && !context.CallbackAnswered)
            await AnswerButtonAsync(context, ct);
        return ok;
    }

    private static void Record(Update update, UpdateTrace trace, bool failed, long started, Activity? activity)
    {
        var handledBy = trace.HandledBy;
        var outcome = failed ? "failed" : handledBy is null or "no action" ? "ignored" : "handled";
        var type = new KeyValuePair<string, object?>("update.type", update.Type.ToString());
        var result = new KeyValuePair<string, object?>("outcome", outcome);

        BotTelemetry.Updates.Add(1, type, result);
        BotTelemetry.UpdateDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
            type, result, new KeyValuePair<string, object?>("action", handledBy ?? "no action"));

        if (activity is null)
            return;

        activity.DisplayName = handledBy is null ? "telegram.update" : "telegram.update " + handledBy;
        activity.SetTag("telegram.update.type", update.Type.ToString());
        activity.SetTag("telegram.update.id", update.Id);
        activity.SetTag("telegram.action", handledBy);
        activity.SetTag("telegram.outcome", outcome);
        if (failed)
            activity.SetStatus(ActivityStatusCode.Error);
    }

    private async Task AnswerButtonAsync(BotContext context, CancellationToken ct)
    {
        try
        {
            await _bot.AnswerCallbackQuery(context.CallbackQuery!.Id, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not answer the button press");
        }

        context.CallbackAnswered = true;
    }
}
