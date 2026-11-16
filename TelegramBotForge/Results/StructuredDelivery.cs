using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Media;
using TelegramBotForge.Views;
using Message = Telegram.Bot.Types.Message;

namespace TelegramBotForge.Results;

internal sealed partial class TelegramResultExecutor
{
    private static async Task DeliverAsync(BotContext context, DeliverMessage result, CancellationToken ct)
    {
        var payload = MessageEvaluation.Evaluate(result.Content);
        var chatId = RequireChatId(context);
        var previous = result.Edit ? result.Previous : null;
        if (previous is not null && previous.ChatId != chatId)
            throw new InvalidOperationException($"View {result.Content.Origin}: the previous delivery belongs to another chat.");
        var target = result.Edit ? previous?.PrimaryMessageId ?? result.MessageId ?? context.CallbackQuery?.Message?.Id : null;
        if (result.Edit && payload.Message.Keyboard is not null and not InlineKeyboardMarkup)
            throw new InvalidOperationException($"View {result.Content.Origin}: an edited message can only have an inline keyboard.");
        var reuseMedia = !payload.Message.Rich && previous is { Rich: false, Media: not null }
            && payload.Attachments.All(attachment => attachment.Source == MediaSource.FileId)
            && previous.Media.SequenceEqual(payload.Attachments);
        var associated = reuseMedia ? previous!.AssociatedMessageIds.ToList() : new List<int>();
        var opened = new List<OpenedFile>();
        var newlySent = new List<int>();
        var committed = false;
        try
        {
            InputRichMessage? rich = null;
            if (payload.Message.Rich)
            {
                var media = new List<InputRichMessageMedia>();
                for (var index = 0; index < payload.Attachments.Length; index++)
                {
                    var attachment = payload.Attachments[index];
                    var file = OpenedFile.Open(attachment);
                    opened.Add(file);
                    media.Add(new InputRichMessageMedia
                    {
                        Id = "m" + index,
                        Media = attachment.Type == MediaType.Photo ? new InputMediaPhoto(file.Input) : new InputMediaVideo(file.Input)
                    });
                }
                rich = new InputRichMessage { Html = payload.Message.Text, Media = media };
            }
            else if (!reuseMedia)
            {
                foreach (var attachment in payload.Attachments)
                {
                    await using var file = OpenedFile.Open(attachment);
                    var sent = attachment.Type == MediaType.Photo
                        ? await context.Bot.SendPhoto(chatId, file.Input, cancellationToken: ct)
                        : await context.Bot.SendVideo(chatId, file.Input, cancellationToken: ct);
                    associated.Add(sent.Id);
                    newlySent.Add(sent.Id);
                }
            }

            var primaryId = target;
            var replace = previous is not null && previous.Rich != payload.Message.Rich
                || target is not null && !payload.Message.Rich && !reuseMedia && payload.Attachments.Length != 0;
            if (target is { } messageId && !replace)
            {
                try
                {
                    var edited = await context.Bot.EditMessageText(chatId, messageId, rich is null ? payload.Message.Text : null!,
                        parseMode: ParseMode.Html, replyMarkup: payload.Message.Keyboard as InlineKeyboardMarkup,
                        richMessage: rich, linkPreviewOptions: LinkPreviewOptions.Disabled, cancellationToken: ct);
                    primaryId = edited.Id;
                }
                catch (ApiRequestException exception) when (IsMessageNotModified(exception)) { primaryId = messageId; }
                catch (ApiRequestException exception) when (IsMessageUnavailable(exception)) { replace = true; }
            }
            if (target is null || replace)
            {
                Message sent = rich is null
                    ? await context.Bot.SendMessage(chatId, payload.Message.Text, parseMode: ParseMode.Html,
                        replyMarkup: payload.Message.Keyboard, linkPreviewOptions: LinkPreviewOptions.Disabled, cancellationToken: ct)
                    : await context.Bot.SendRichMessage(chatId, rich, replyMarkup: payload.Message.Keyboard, cancellationToken: ct);
                primaryId = sent.Id;
                newlySent.Add(sent.Id);
            }
            var receipt = new MessageDelivery(chatId, primaryId!.Value, associated, payload.Message.Rich) { Media = payload.Attachments };
            context.DeliveredMessages.Add(receipt);
            committed = true;
            var cleanup = previous is not null && !reuseMedia ? previous.AssociatedMessageIds.ToList() : new List<int>();
            if (replace && target is { } oldPrimary)
                cleanup.Add(oldPrimary);
            var failures = new List<Exception>();
            foreach (var old in cleanup)
            {
                try { await DeleteOutputAsync(context, chatId, old, ct); }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (result.Content.Delivered is { } delivered)
            {
                try { await delivered(receipt, ct); }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (failures.Count != 0)
                throw new AggregateException($"View {result.Content.Origin}: output was delivered, but cleanup or the delivery callback failed. Receipts contain the actual output IDs.", failures);
        }
        catch (Exception exception)
        {
            exception.Data["TelegramBotForge.View"] = result.Content.Origin;
            if (!committed)
                foreach (var messageId in newlySent)
                {
                    try { await DeleteOutputAsync(context, chatId, messageId, CancellationToken.None); }
                    catch (Exception cleanupException) { exception.Data[$"TelegramBotForge.Cleanup.{messageId}"] = cleanupException; }
                }
            throw;
        }
        finally
        {
            foreach (var file in opened)
                await file.DisposeAsync();
        }
    }

    private static async Task DeleteOutputAsync(BotContext context, long chatId, int messageId, CancellationToken ct)
    {
        try { await context.Bot.DeleteMessage(chatId, messageId, cancellationToken: ct); }
        catch (ApiRequestException exception) when (exception.ErrorCode == 400 &&
            exception.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase)) { }
    }
}
