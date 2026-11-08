using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.Payments;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Media;
using TelegramBotForge.Payments;

namespace TelegramBotForge.Results;

/// <summary>
/// Sends results to Telegram. Register your own implementation to support your own result types.
/// </summary>
public interface IBotResultExecutor
{
    /// <summary>
    /// Executes the result for the update of <paramref name="context"/>.
    /// </summary>
    Task ExecuteAsync(BotContext context, IBotResult result, CancellationToken ct);
}

/// <summary>
/// The only place where results become Telegram API calls.
/// </summary>
internal sealed class TelegramResultExecutor : IBotResultExecutor
{
    private readonly Microsoft.Extensions.Logging.ILogger? _logger;
    private int _warnedIgnoredAnswer;

    public TelegramResultExecutor(Microsoft.Extensions.Logging.ILogger<TelegramResultExecutor>? logger = null) => _logger = logger;

    public async Task ExecuteAsync(BotContext context, IBotResult result, CancellationToken ct)
    {
        switch (result)
        {
            case MultiResult multi:
                foreach (var nested in multi.Results)
                    await ExecuteAsync(context, nested, ct);
                break;

            case SendText { Rich: true } send:
                await SendRichAsync(context, send.Text, send.Keyboard, ct);
                break;

            case SendText send:
                await SendAsync(context, send.Text, send.Keyboard, send.ParseMode, send.DisableLinkPreview, ct);
                break;

            case EditText { Rich: true } edit:
                await EditRichAsync(context, edit, ct);
                break;

            case EditText edit:
                await EditAsync(context, edit, ct);
                break;

            case SendMedia send:
                await SendMediaAsync(context, send.Media, send.Caption, send.Keyboard, send.ParseMode, ct);
                break;

            case EditMedia edit:
                await EditMediaAsync(context, edit, ct);
                break;

            case StarsInvoice invoice:
                await SendInvoiceAsync(context, invoice, ct);
                break;

            case AnswerPreCheckout checkout:
            {
                var query = context.Update.PreCheckoutQuery
                            ?? throw new InvalidOperationException("AnswerPreCheckout can only be returned for a check before payment.");
                await context.Bot.AnswerPreCheckoutQuery(
                    query.Id,
                    errorMessage: checkout.Ok ? null : checkout.ErrorMessage ?? "Payment verification failed.",
                    cancellationToken: ct);
                break;
            }

            case RefundStars refund:
                await context.Bot.RefundStarPayment(
                    userId: refund.UserId ?? context.UserId ?? throw new InvalidOperationException("RefundStars needs a UserId: this update has no user."),
                    telegramPaymentChargeId: refund.ChargeId,
                    cancellationToken: ct);
                break;

            case AnswerInline inline:
            {
                var query = context.InlineQuery
                            ?? throw new InvalidOperationException("AnswerInline can only be returned for an inline query.");
                await context.Bot.AnswerInlineQuery(
                    query.Id,
                    inline.Results,
                    cacheTime: inline.CacheSeconds,
                    isPersonal: inline.IsPersonal,
                    nextOffset: inline.NextOffset,
                    button: inline.Button,
                    cancellationToken: ct);
                break;
            }

            case AnswerCallback answer:
                await AnswerAsync(context, answer, ct);
                break;

            case DeleteMessage delete:
            {
                var messageId = delete.MessageId
                                ?? context.CallbackQuery?.Message?.MessageId
                                ?? context.Message?.MessageId
                                ?? throw new InvalidOperationException("DeleteMessage needs a MessageId: this update has no message.");
                await context.Bot.DeleteMessage(chatId: RequireChatId(context), messageId: messageId, cancellationToken: ct);
                break;
            }

            default:
                throw new NotSupportedException(
                    $"{result.GetType().FullName} is not a result {nameof(TelegramResultExecutor)} can send. " +
                    $"Register your own {nameof(IBotResultExecutor)} to support it.");
        }
    }

    private static async Task SendAsync(
        BotContext context, string text, ReplyMarkup? keyboard, ParseMode parseMode, bool disableLinkPreview, CancellationToken ct)
    {
        await context.Bot.SendMessage(
            chatId: RequireChatId(context),
            text: text,
            parseMode: parseMode,
            replyMarkup: keyboard,
            linkPreviewOptions: GetLinkPreviewOptions(disableLinkPreview),
            cancellationToken: ct);
    }

    private static async Task SendRichAsync(BotContext context, string html, ReplyMarkup? keyboard, CancellationToken ct)
    {
        await context.Bot.SendRichMessage(
            chatId: RequireChatId(context),
            richMessage: new InputRichMessage { Html = html },
            replyMarkup: keyboard,
            cancellationToken: ct);
    }

    private static async Task EditRichAsync(BotContext context, EditText edit, CancellationToken ct)
    {
        var messageId = edit.MessageId ?? context.CallbackQuery?.Message?.MessageId;
        if (messageId is null)
        {
            await SendRichAsync(context, edit.Text, edit.Keyboard, ct);
            return;
        }

        try
        {
            await context.Bot.EditMessageText(
                chatId: RequireChatId(context),
                messageId: messageId.Value,
                text: null!,
                replyMarkup: edit.Keyboard,
                richMessage: new InputRichMessage { Html = edit.Text },
                cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (IsMessageNotModified(ex))
        {
            // The message already looks like this.
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400)
        {
            // The message can no longer be edited, or is not a rich message.
            await SendRichAsync(context, edit.Text, edit.Keyboard, ct);
        }
    }

    private static async Task EditAsync(BotContext context, EditText edit, CancellationToken ct)
    {
        var messageId = edit.MessageId ?? context.CallbackQuery?.Message?.MessageId;
        if (messageId is null)
        {
            await SendAsync(context, edit.Text, edit.Keyboard, edit.ParseMode, edit.DisableLinkPreview, ct);
            return;
        }

        try
        {
            await context.Bot.EditMessageText(
                chatId: RequireChatId(context),
                messageId: messageId.Value,
                text: edit.Text,
                parseMode: edit.ParseMode,
                replyMarkup: edit.Keyboard,
                linkPreviewOptions: GetLinkPreviewOptions(edit.DisableLinkPreview),
                cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (IsMessageNotModified(ex))
        {
            // The message already looks like this.
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400)
        {
            // The message can no longer be edited, for example because it is too old or was deleted.
            await SendAsync(context, edit.Text, edit.Keyboard, edit.ParseMode, edit.DisableLinkPreview, ct);
        }
    }

    private async Task AnswerAsync(BotContext context, AnswerCallback answer, CancellationToken ct)
    {
        var query = context.CallbackQuery
                    ?? throw new InvalidOperationException("AnswerCallback can only be returned for a button press.");

        // With CallbackAckMode.Immediate the button was answered before the action ran.
        if (context.CallbackAnswered)
        {
            // Telegram takes one answer per press, so a notification can no longer be shown. Said once, so it is noticed.
            if (answer.Text is not null && _logger is not null && Interlocked.Exchange(ref _warnedIgnoredAnswer, 1) == 0)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(_logger,
                    "Answer(\"{Text}\") of {Action} was not shown: buttons are answered as soon as they are pressed " +
                    "(CallbackAckMode.Immediate). Call .AckCallbacks(CallbackAckMode.Manual) to show notifications and alerts. " +
                    "This warning is logged once.", answer.Text, context.HandledBy ?? "the action");
            }

            return;
        }

        await context.Bot.AnswerCallbackQuery(
            callbackQueryId: query.Id,
            text: answer.Text,
            showAlert: answer.ShowAlert,
            cancellationToken: ct);
        context.CallbackAnswered = true;
    }

    private static async Task SendMediaAsync(
        BotContext context, MediaFile media, string? caption, ReplyMarkup? keyboard, ParseMode parseMode, CancellationToken ct)
    {
        if (caption is not null && media.Type is MediaType.VideoNote or MediaType.Sticker)
            throw new InvalidOperationException($"Telegram does not allow a caption on a {media.Type}.");

        var chatId = RequireChatId(context);
        await using var file = OpenedFile.Open(media);

        switch (media.Type)
        {
            case MediaType.Photo:
                await context.Bot.SendPhoto(chatId: chatId, photo: file.Input, caption: caption, parseMode: parseMode,
                    replyMarkup: keyboard, cancellationToken: ct);
                break;
            case MediaType.Video:
                await context.Bot.SendVideo(chatId: chatId, video: file.Input, caption: caption, parseMode: parseMode,
                    replyMarkup: keyboard, cancellationToken: ct);
                break;
            case MediaType.Animation:
                await context.Bot.SendAnimation(chatId: chatId, animation: file.Input, caption: caption, parseMode: parseMode,
                    replyMarkup: keyboard, cancellationToken: ct);
                break;
            case MediaType.Document:
                await context.Bot.SendDocument(chatId: chatId, document: file.Input, caption: caption, parseMode: parseMode,
                    replyMarkup: keyboard, cancellationToken: ct);
                break;
            case MediaType.Audio:
                await context.Bot.SendAudio(chatId: chatId, audio: file.Input, caption: caption, parseMode: parseMode,
                    replyMarkup: keyboard, cancellationToken: ct);
                break;
            case MediaType.Voice:
                await context.Bot.SendVoice(chatId: chatId, voice: file.Input, caption: caption, parseMode: parseMode,
                    replyMarkup: keyboard, cancellationToken: ct);
                break;
            case MediaType.VideoNote:
                await context.Bot.SendVideoNote(chatId: chatId, videoNote: file.Input, replyMarkup: keyboard, cancellationToken: ct);
                break;
            case MediaType.Sticker:
                await context.Bot.SendSticker(chatId: chatId, sticker: file.Input, replyMarkup: keyboard, cancellationToken: ct);
                break;
            default:
                throw new NotSupportedException($"Sending {media.Type} is not supported.");
        }
    }

    private static async Task EditMediaAsync(BotContext context, EditMedia edit, CancellationToken ct)
    {
        var messageId = edit.MessageId ?? context.CallbackQuery?.Message?.MessageId;
        var editable = edit.Media.Type is MediaType.Photo or MediaType.Video or MediaType.Animation
            or MediaType.Document or MediaType.Audio;

        if (messageId is null || !editable)
        {
            await SendMediaAsync(context, edit.Media, edit.Caption, edit.Keyboard, edit.ParseMode, ct);
            return;
        }

        try
        {
            await using var file = OpenedFile.Open(edit.Media);
            InputMedia media = edit.Media.Type switch
            {
                MediaType.Photo => new InputMediaPhoto(file.Input) { Caption = edit.Caption, ParseMode = edit.ParseMode },
                MediaType.Video => new InputMediaVideo(file.Input) { Caption = edit.Caption, ParseMode = edit.ParseMode },
                MediaType.Animation => new InputMediaAnimation(file.Input) { Caption = edit.Caption, ParseMode = edit.ParseMode },
                MediaType.Document => new InputMediaDocument(file.Input) { Caption = edit.Caption, ParseMode = edit.ParseMode },
                _ => new InputMediaAudio(file.Input) { Caption = edit.Caption, ParseMode = edit.ParseMode }
            };

            await context.Bot.EditMessageMedia(
                chatId: RequireChatId(context),
                messageId: messageId.Value,
                media: media,
                replyMarkup: edit.Keyboard,
                cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (IsMessageNotModified(ex))
        {
            // The message already looks like this.
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400)
        {
            // For example the message is a text message, which Telegram cannot turn into a media message.
            await SendMediaAsync(context, edit.Media, edit.Caption, edit.Keyboard, edit.ParseMode, ct);
        }
    }

    private static async Task SendInvoiceAsync(BotContext context, StarsInvoice invoice, CancellationToken ct)
    {
        var products = context.Services.GetService<IStarsProducts>()
                       ?? throw new InvalidOperationException("Stars are not enabled: call AddStars() at startup.");
        var product = await products.FindAsync(invoice.ProductCode, ct)
                      ?? throw new InvalidOperationException($"Stars product \"{invoice.ProductCode}\" does not exist.");
        var buyer = context.UserId ?? throw new InvalidOperationException("An invoice needs a user: this update has none.");

        await context.Bot.SendInvoice(
            chatId: RequireChatId(context),
            title: product.Title,
            description: product.Description,
            payload: StarsPayload.New(product.Code, buyer).ToString(),
            currency: StarsPayload.Currency,
            prices: [new LabeledPrice(product.Title, product.Price)],
            providerToken: string.Empty,
            cancellationToken: ct);
    }

    // A check before payment has no chat; answers go to the buyer's private chat.
    private static long RequireChatId(BotContext context) =>
        context.ChatId
        ?? context.Update.PreCheckoutQuery?.From.Id
        ?? throw new InvalidOperationException("This update has no chat to answer in.");

    private static LinkPreviewOptions? GetLinkPreviewOptions(bool disableLinkPreview) =>
        disableLinkPreview ? LinkPreviewOptions.Disabled : null;

    private static bool IsMessageNotModified(ApiRequestException exception) =>
        exception.ErrorCode == 400 &&
        exception.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The file of outgoing media, ready to send. Owns the stream of a local file until it was sent.
    /// </summary>
    private sealed class OpenedFile : IAsyncDisposable
    {
        private readonly Stream? _stream;

        private OpenedFile(InputFile input, Stream? stream)
        {
            Input = input;
            _stream = stream;
        }

        public InputFile Input { get; }

        public static OpenedFile Open(MediaFile media) => media.Source switch
        {
            MediaSource.FileId => new OpenedFile(InputFile.FromFileId(media.Value), null),
            MediaSource.Url => new OpenedFile(InputFile.FromUri(new Uri(media.Value)), null),
            MediaSource.Path => OpenPath(media.Value),
            _ => throw new NotSupportedException($"Unknown media source {media.Source}.")
        };

        public ValueTask DisposeAsync() => _stream?.DisposeAsync() ?? ValueTask.CompletedTask;

        private static OpenedFile OpenPath(string path)
        {
            var resolved = ResolvePath(path);
            var stream = System.IO.File.OpenRead(resolved);
            return new OpenedFile(InputFile.FromStream(stream, Path.GetFileName(resolved)), stream);
        }

        /// <summary>
        /// Relative paths are looked up next to the application first, then in the working directory.
        /// </summary>
        private static string ResolvePath(string path)
        {
            if (Path.IsPathRooted(path))
                return path;

            var nextToApp = Path.Combine(AppContext.BaseDirectory, path);
            return System.IO.File.Exists(nextToApp) ? nextToApp : Path.Combine(Directory.GetCurrentDirectory(), path);
        }
    }
}
