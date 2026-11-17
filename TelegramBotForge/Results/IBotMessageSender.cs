using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Message = TelegramBotForge.Views.Message;

namespace TelegramBotForge.Results;

/// <summary>Delivers structured messages outside update processing, with one DI scope per operation.</summary>
/// <remarks>MessageDeliveryException carries a confirmed receipt when post-delivery processing fails. Do not resend it.</remarks>
public interface IBotMessageSender
{
    Task<MessageDelivery> SendAsync(long chatId, Message message, CancellationToken ct = default);
    Task<MessageDelivery> EditAsync(long chatId, int messageId, Message message, CancellationToken ct = default);
    Task<MessageDelivery> EditAsync(MessageDelivery previous, Message message, CancellationToken ct = default);
}

internal sealed class TelegramBotMessageSender(ITelegramBotClient bot, IServiceScopeFactory scopes) : IBotMessageSender
{
    public Task<MessageDelivery> SendAsync(long chatId, Message message, CancellationToken ct = default) =>
        ExecuteAsync(chatId, new DeliverMessage(message), ct);

    public Task<MessageDelivery> EditAsync(long chatId, int messageId, Message message, CancellationToken ct = default) =>
        ExecuteAsync(chatId, new DeliverMessage(message, Edit: true, MessageId: messageId), ct);

    public Task<MessageDelivery> EditAsync(MessageDelivery previous, Message message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return ExecuteAsync(previous.ChatId, new DeliverMessage(message, Edit: true, Previous: previous), ct);
    }

    private async Task<MessageDelivery> ExecuteAsync(long chatId, DeliverMessage result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result.Content);
        var scope = scopes.CreateAsyncScope();
        var update = new Update { Message = new Telegram.Bot.Types.Message { Chat = new Chat { Id = chatId, Type = chatId > 0 ? ChatType.Private : ChatType.Supergroup } } };
        var context = new BotContext(update, bot, scope.ServiceProvider, ct);
        await ResultLifecycleExtensions.WithDeliveryAsync(context, () => ResultLifecycleExtensions.FinishAsync(async () =>
        {
            var executor = scope.ServiceProvider.GetRequiredService<IBotResultExecutor>();
            await ResultLifecycleExtensions.ExecuteAsync(context, result, executor.ExecuteAsync, ct);
        }, _ => scope.DisposeAsync().AsTask(), context));
        return context.Deliveries.Single();
    }
}
