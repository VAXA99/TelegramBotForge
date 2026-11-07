using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using TelegramBotForge.Logging;
using TelegramBotForge.Middleware;
using TelegramBotForge.Results;

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

    public UpdateDispatcher(
        IServiceScopeFactory scopes,
        ITelegramBotClient bot,
        BotPipeline pipeline,
        ILogger<UpdateDispatcher> logger)
    {
        _scopes = scopes;
        _bot = bot;
        _pipeline = pipeline;
        _logger = logger;
    }

    /// <param name="update">The update.</param>
    /// <param name="callbackAnswered">The button press was already answered when it was queued.</param>
    /// <param name="ct">Cancelled when the bot stops.</param>
    public async Task DispatchAsync(Update update, bool callbackAnswered, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var context = new BotContext(update, _bot, scope.ServiceProvider, ct) { CallbackAnswered = callbackAnswered };
        using var logScope = _logger.BeginScope(new UpdateLogScope(context.ChatId ?? 0, context.UserId ?? 0));

        IBotResult? result;
        try
        {
            result = await _pipeline.InvokeAsync(context, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Only reached when ErrorMiddleware was removed or rethrows.
            _logger.LogError(ex, "Update {UpdateId} failed", update.Id);
            result = null;
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
            return;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 403)
        {
            _logger.LogWarning("Telegram refused the answer to update {UpdateId}: {Reason}", update.Id, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not send the answer ({Result}) to update {UpdateId}",
                UpdateDescriber.DescribeResult(result), update.Id);
        }

        // Every pressed button gets an answer, so its loading spinner never hangs.
        if (context.CallbackQuery is not null && !context.CallbackAnswered)
            await AnswerButtonAsync(context, ct);
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
