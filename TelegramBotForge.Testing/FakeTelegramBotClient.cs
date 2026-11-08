using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBotForge.Testing;

/// <summary>
/// A Telegram client that sends nothing: it records every request and answers it the way Telegram would,
/// and keeps the messages the bot sent in each chat, with their edits.
/// </summary>
/// <remarks>
/// Answers you need to control, or errors to simulate, are set with <see cref="Handle{TRequest}"/>.
/// </remarks>
public sealed class FakeTelegramBotClient : ITelegramBotClient
{
    private readonly object _gate = new();
    private readonly List<IRequest> _requests = [];
    private readonly Dictionary<long, List<Message>> _chats = [];
    private readonly ConcurrentDictionary<Type, Func<IRequest, object?>> _handlers = new();
    private int _nextMessageId = 1000;

    /// <summary>
    /// Initializes a new instance of the <see cref="FakeTelegramBotClient"/> class.
    /// </summary>
    /// <param name="botId">The id of the bot.</param>
    /// <param name="username">The username of the bot, without <c>@</c>.</param>
    public FakeTelegramBotClient(long botId = 7000000001, string username = "test_bot")
    {
        Me = new User { Id = botId, IsBot = true, FirstName = "Test bot", Username = username };
    }

    /// <summary>
    /// Gets the bot user, which <c>GetMe</c> returns.
    /// </summary>
    public User Me { get; }

    /// <inheritdoc/>
    public bool LocalBotServer => false;

    /// <inheritdoc/>
    public long BotId => Me.Id;

    /// <inheritdoc/>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <inheritdoc/>
    public IExceptionParser ExceptionsParser { get; set; } = new DefaultExceptionParser();

    /// <inheritdoc/>
#pragma warning disable CS0067 // A fake makes no HTTP requests, so these never fire.
    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest;

    /// <inheritdoc/>
    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived;
#pragma warning restore CS0067

    /// <summary>
    /// Gets every request made so far, in order.
    /// </summary>
    public IReadOnlyList<IRequest> Requests
    {
        get
        {
            lock (_gate)
                return _requests.ToList();
        }
    }

    /// <summary>
    /// Gets the requests of one type, in order, for example <c>RequestsOf&lt;SetMyCommandsRequest&gt;()</c>.
    /// </summary>
    public IReadOnlyList<TRequest> RequestsOf<TRequest>() where TRequest : IRequest => Requests.OfType<TRequest>().ToList();

    /// <summary>
    /// Gets the messages the bot sent to a chat and has not deleted, oldest first, as they look after edits.
    /// </summary>
    public IReadOnlyList<Message> MessagesIn(long chatId)
    {
        lock (_gate)
            return _chats.TryGetValue(chatId, out var messages) ? messages.ToList() : [];
    }

    /// <summary>
    /// Answers requests of type <typeparamref name="TRequest"/> with your own function: return the response, or throw
    /// to simulate an error, for example <c>throw new ApiRequestException("Forbidden: bot was blocked by the user", 403)</c>.
    /// Return <c>null</c> to fall back to the default answer.
    /// </summary>
    public FakeTelegramBotClient Handle<TRequest>(Func<TRequest, object?> handler) where TRequest : IRequest
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[typeof(TRequest)] = request => handler((TRequest)request);
        return this;
    }

    /// <summary>
    /// Forgets the recorded requests. The messages in the chats are kept.
    /// </summary>
    public void ClearRequests()
    {
        lock (_gate)
            _requests.Clear();
    }

    /// <inheritdoc/>
    public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
            _requests.Add(request);

        if (_handlers.TryGetValue(request.GetType(), out var handler) && handler(request) is { } custom)
            return Task.FromResult((TResponse)custom);

        return Task.FromResult(DefaultResponse(request));
    }

    /// <inheritdoc/>
    public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <inheritdoc/>
    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private TResponse DefaultResponse<TResponse>(IRequest<TResponse> request)
    {
        object? response = request switch
        {
            GetMeRequest => Me,
            EditMessageTextRequest edit => Edit(edit.ChatId, edit.MessageId, message =>
            {
                message.Text = edit.Text ?? message.Text;
                message.ReplyMarkup = edit.ReplyMarkup;
            }),
            EditMessageCaptionRequest edit => Edit(edit.ChatId, edit.MessageId, message =>
            {
                message.Caption = edit.Caption;
                message.ReplyMarkup = edit.ReplyMarkup;
            }),
            EditMessageMediaRequest edit => Edit(edit.ChatId, edit.MessageId, message =>
            {
                message.Caption = edit.Media.Caption;
                message.ReplyMarkup = edit.ReplyMarkup;
            }),
            EditMessageReplyMarkupRequest edit => Edit(edit.ChatId, edit.MessageId, message => message.ReplyMarkup = edit.ReplyMarkup),
            DeleteMessageRequest delete => Delete(delete.ChatId, delete.MessageId),
            IChatTargetable target when typeof(TResponse) == typeof(Message) => Send(target.ChatId, request),
            _ => null
        };

        if (response is not null)
            return (TResponse)response;

        if (typeof(TResponse) == typeof(bool))
            return (TResponse)(object)true;
        if (typeof(TResponse).IsArray)
            return (TResponse)(object)Array.CreateInstance(typeof(TResponse).GetElementType()!, 0);

        return default!;
    }

    private Message Send(ChatId chatId, IRequest request)
    {
        var id = chatId.Identifier ?? throw new NotSupportedException("The fake client only sends to numeric chat ids.");
        var message = new Message
        {
            Id = Interlocked.Increment(ref _nextMessageId),
            Date = DateTime.UtcNow,
            From = Me,
            Chat = new Chat { Id = id, Type = id > 0 ? ChatType.Private : ChatType.Supergroup }
        };

        switch (request)
        {
            case SendMessageRequest send:
                message.Text = send.Text;
                message.ReplyMarkup = send.ReplyMarkup as InlineKeyboardMarkup;
                break;
            case SendInvoiceRequest invoice:
                message.Invoice = new Telegram.Bot.Types.Payments.Invoice
                {
                    Title = invoice.Title,
                    Description = invoice.Description,
                    Currency = invoice.Currency,
                    TotalAmount = invoice.Prices.Sum(price => price.Amount)
                };
                message.ReplyMarkup = invoice.ReplyMarkup;
                break;
            default:
                // Media and the rest: the caption and buttons are what tests look at.
                var type = request.GetType();
                message.Caption = type.GetProperty("Caption")?.GetValue(request) as string;
                message.ReplyMarkup = type.GetProperty("ReplyMarkup")?.GetValue(request) as InlineKeyboardMarkup;
                break;
        }

        lock (_gate)
        {
            if (!_chats.TryGetValue(id, out var messages))
                _chats[id] = messages = [];
            messages.Add(message);
        }

        return message;
    }

    private Message Edit(ChatId? chatId, int messageId, Action<Message> change)
    {
        lock (_gate)
        {
            var message = chatId?.Identifier is { } id && _chats.TryGetValue(id, out var messages)
                ? messages.FirstOrDefault(candidate => candidate.Id == messageId)
                : null;

            // Telegram answers 400 for a message it does not know; the library then sends a new message instead.
            if (message is null)
                throw new ApiRequestException("Bad Request: message to edit not found", 400);

            change(message);
            message.EditDate = DateTime.UtcNow;
            return message;
        }
    }

    private bool Delete(ChatId chatId, int messageId)
    {
        lock (_gate)
        {
            if (chatId.Identifier is { } id && _chats.TryGetValue(id, out var messages))
                messages.RemoveAll(message => message.Id == messageId);
        }

        return true;
    }
}
