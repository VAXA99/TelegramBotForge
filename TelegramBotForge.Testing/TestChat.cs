using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Payments;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.State;

namespace TelegramBotForge.Testing;

/// <summary>
/// A user in a chat with the bot: sends messages, presses buttons and pays invoices like a real user would.
/// </summary>
public sealed class TestChat
{
    private readonly BotTestHost _host;
    private readonly List<Message> _sent = [];

    internal TestChat(BotTestHost host, Chat chat, User user)
    {
        _host = host;
        Chat = chat;
        User = user;
    }

    /// <summary>
    /// Gets the chat.
    /// </summary>
    public Chat Chat { get; }

    /// <summary>
    /// Gets the user.
    /// </summary>
    public User User { get; }

    /// <summary>
    /// Gets the messages the bot sent to this chat and has not deleted, oldest first, as they look after edits.
    /// </summary>
    public IReadOnlyList<Message> Messages => _host.Telegram.MessagesIn(Chat.Id);

    /// <summary>
    /// Gets the newest message of the bot in this chat, or <c>null</c>.
    /// </summary>
    public Message? LastMessage => Messages.LastOrDefault();

    /// <summary>
    /// Sends a text message, or a command such as <c>/start ref_42</c>.
    /// </summary>
    public Task<UpdateResult> SendText(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SendMessage(message => message.Text = text, ct);
    }

    /// <summary>
    /// Sends a location.
    /// </summary>
    public Task<UpdateResult> SendLocation(double latitude, double longitude, CancellationToken ct = default) =>
        SendMessage(message => message.Location = new Location { Latitude = latitude, Longitude = longitude }, ct);

    /// <summary>
    /// Sends a photo with an optional caption. Its file id is made up.
    /// </summary>
    public Task<UpdateResult> SendPhoto(string? caption = null, CancellationToken ct = default) =>
        SendMessage(message =>
        {
            message.Photo = [new PhotoSize { FileId = "photo-" + Guid.NewGuid().ToString("N"), FileUniqueId = Guid.NewGuid().ToString("N"), Width = 800, Height = 600 }];
            message.Caption = caption;
        }, ct);

    /// <summary>
    /// Shares a contact, by default the user's own phone number, as a "share my phone number" button does.
    /// </summary>
    /// <param name="phoneNumber">The phone number.</param>
    /// <param name="ownContact"><c>true</c> for the user's own contact, <c>false</c> for someone else's from the address book.</param>
    /// <param name="ct">A token to cancel the test.</param>
    public Task<UpdateResult> SendContact(string phoneNumber = "+10000000000", bool ownContact = true, CancellationToken ct = default) =>
        SendMessage(message => message.Contact = new Contact
        {
            PhoneNumber = phoneNumber,
            FirstName = ownContact ? User.FirstName : "Someone",
            UserId = ownContact ? User.Id : null
        }, ct);

    /// <summary>
    /// Sends an album of photos. The caption is on the first one, like in Telegram.
    /// </summary>
    /// <param name="photos">How many photos, 2 to 10.</param>
    /// <param name="caption">The caption of the album.</param>
    /// <param name="ct">A token to cancel the test.</param>
    public Task<UpdateResult> SendAlbum(int photos = 3, string? caption = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(photos, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(photos, 10);

        var group = Guid.NewGuid().ToString("N");
        var items = Enumerable.Range(0, photos).Select(index =>
        {
            var message = NewMessage();
            message.MediaGroupId = group;
            message.Photo = [new PhotoSize { FileId = "photo-" + Guid.NewGuid().ToString("N"), FileUniqueId = Guid.NewGuid().ToString("N"), Width = 800, Height = 600 }];
            message.Caption = index == 0 ? caption : null;
            Remember(message);
            return message;
        }).ToList();

        return _host.SendAlbum(items, ct);
    }

    /// <summary>
    /// Edits a message the user sent, by default the newest text, as Telegram reports an edit.
    /// </summary>
    /// <param name="text">The new text.</param>
    /// <param name="message">The message to edit; by default the user's newest text message.</param>
    /// <param name="ct">A token to cancel the test.</param>
    /// <exception cref="InvalidOperationException">The user sent no text message yet.</exception>
    public Task<UpdateResult> EditText(string text, Message? message = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var original = message
                       ?? _sent.LastOrDefault(sent => sent.Text is not null)
                       ?? throw new InvalidOperationException("The user sent no text message to edit.");

        var edited = NewMessage();
        edited.Id = original.Id;
        edited.Date = original.Date;
        edited.EditDate = DateTime.UtcNow;
        edited.Text = text;
        return _host.SendUpdate(new Update { EditedMessage = edited }, ct);
    }

    /// <summary>
    /// Types an inline query, <c>@YourBot query</c>, in a chat of the user.
    /// </summary>
    /// <param name="query">What the user typed after the bot's username.</param>
    /// <param name="offset">The offset of the page, from the bot's previous <c>NextOffset</c>.</param>
    /// <param name="ct">A token to cancel the test.</param>
    public Task<UpdateResult> SendInlineQuery(string query, string offset = "", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _host.SendUpdate(new Update
        {
            InlineQuery = new InlineQuery { Id = Guid.NewGuid().ToString("N"), From = User, Query = query, Offset = offset }
        }, ct);
    }

    /// <summary>
    /// Blocks the bot, as a user does with "Stop bot". Only for private chats.
    /// </summary>
    public Task<UpdateResult> BlockBot(CancellationToken ct = default) =>
        ChangeMembership(new ChatMemberMember { User = _host.Telegram.Me }, new ChatMemberBanned { User = _host.Telegram.Me }, ct);

    /// <summary>
    /// Unblocks the bot, as a user does with "Restart bot". Only for private chats.
    /// </summary>
    public Task<UpdateResult> UnblockBot(CancellationToken ct = default) =>
        ChangeMembership(new ChatMemberBanned { User = _host.Telegram.Me }, new ChatMemberMember { User = _host.Telegram.Me }, ct);

    /// <summary>
    /// Adds the bot to this group, by this user. Only for groups.
    /// </summary>
    public Task<UpdateResult> AddBot(CancellationToken ct = default) =>
        ChangeMembership(new ChatMemberLeft { User = _host.Telegram.Me }, new ChatMemberMember { User = _host.Telegram.Me }, ct);

    /// <summary>
    /// Removes the bot from this group, by this user. Only for groups.
    /// </summary>
    public Task<UpdateResult> RemoveBot(CancellationToken ct = default) =>
        ChangeMembership(new ChatMemberMember { User = _host.Telegram.Me }, new ChatMemberLeft { User = _host.Telegram.Me }, ct);

    /// <summary>
    /// Sends a message that you fill in: the id, date, chat and sender are set for you.
    /// </summary>
    public Task<UpdateResult> SendMessage(Action<Message> fill, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fill);
        var message = NewMessage();
        fill(message);
        Remember(message);
        return _host.SendUpdate(new Update { Message = message }, ct);
    }

    /// <summary>
    /// Presses the button with this label on the newest bot message that has it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No message of the bot in this chat has such a button.</exception>
    public Task<UpdateResult> PressButton(string label, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        foreach (var message in Messages.Reverse())
        {
            var button = (message.ReplyMarkup as InlineKeyboardMarkup)?.InlineKeyboard
                .SelectMany(row => row)
                .FirstOrDefault(candidate => candidate.Text == label && candidate.CallbackData is not null);
            if (button is not null)
                return SendCallback(button.CallbackData!, message, ct);
        }

        var labels = Messages.SelectMany(message => (message.ReplyMarkup as InlineKeyboardMarkup)?.InlineKeyboard.SelectMany(row => row) ?? [])
            .Select(button => $"\"{button.Text}\"");
        throw new InvalidOperationException($"No message of the bot has a \"{label}\" button. Buttons: {string.Join(", ", labels)}.");
    }

    /// <summary>
    /// Presses a button with this data, on the newest bot message or on <paramref name="message"/>.
    /// </summary>
    public Task<UpdateResult> SendCallback(string data, Message? message = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var query = new CallbackQuery
        {
            Id = Guid.NewGuid().ToString("N"),
            From = User,
            Message = message ?? LastMessage ?? NewMessage(),
            ChatInstance = Chat.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Data = data
        };
        return _host.SendUpdate(new Update { CallbackQuery = query }, ct);
    }

    /// <summary>
    /// Pays the newest invoice the bot sent to this chat, like Telegram does: first the check before payment and,
    /// if the bot accepts it, the payment.
    /// </summary>
    /// <param name="chargeId">The charge id of the payment, or <c>null</c> for a new one. Repeat one to test duplicate notices.</param>
    /// <param name="ct">A token to cancel the test.</param>
    /// <returns>The answers to the check and, when it was accepted, to the payment.</returns>
    /// <exception cref="InvalidOperationException">The bot sent no invoice to this chat.</exception>
    public async Task<PaymentResult> PayInvoice(string? chargeId = null, CancellationToken ct = default)
    {
        var invoice = _host.Telegram.RequestsOf<SendInvoiceRequest>()
                          .LastOrDefault(request => request.ChatId.Identifier == Chat.Id)
                      ?? throw new InvalidOperationException("The bot sent no invoice to this chat.");
        var amount = invoice.Prices.Sum(price => price.Amount);

        var checkout = await _host.SendUpdate(new Update
        {
            PreCheckoutQuery = new PreCheckoutQuery
            {
                Id = Guid.NewGuid().ToString("N"),
                From = User,
                Currency = invoice.Currency,
                TotalAmount = amount,
                InvoicePayload = invoice.Payload
            }
        }, ct);

        if (checkout.CheckoutAnswer is not { Ok: true })
            return new PaymentResult(checkout, null);

        var paid = await SendMessage(message => message.SuccessfulPayment = new SuccessfulPayment
        {
            Currency = invoice.Currency,
            TotalAmount = amount,
            InvoicePayload = invoice.Payload,
            TelegramPaymentChargeId = chargeId ?? "charge-" + Guid.NewGuid().ToString("N"),
            ProviderPaymentChargeId = string.Empty
        }, ct);

        return new PaymentResult(checkout, paid);
    }

    /// <summary>
    /// Gets the conversation state of the user in this chat, or <c>null</c> when there is none.
    /// </summary>
    public async Task<ChatState?> GetState(CancellationToken ct = default) =>
        await _host.Services.GetRequiredService<IStateStorage>().GetAsync(new StateKey(Chat.Id, User.Id), ct);

    private Task<UpdateResult> ChangeMembership(ChatMember before, ChatMember after, CancellationToken ct) =>
        _host.SendUpdate(new Update
        {
            MyChatMember = new ChatMemberUpdated
            {
                Chat = Chat,
                From = User,
                Date = DateTime.UtcNow,
                OldChatMember = before,
                NewChatMember = after
            }
        }, ct);

    private void Remember(Message message)
    {
        lock (_sent)
            _sent.Add(message);
    }

    private Message NewMessage() => new()
    {
        Id = _host.NextMessageId(),
        Date = DateTime.UtcNow,
        Chat = Chat,
        From = User
    };
}

/// <summary>
/// The answers of the bot to a payment made with <see cref="TestChat.PayInvoice"/>.
/// </summary>
/// <param name="Checkout">The answer to the check before payment.</param>
/// <param name="Payment">The answer to the payment, or <c>null</c> when the check declined it.</param>
public sealed record PaymentResult(UpdateResult Checkout, UpdateResult? Payment)
{
    /// <summary>
    /// Gets a value indicating whether the bot accepted the order.
    /// </summary>
    public bool Accepted => Checkout.CheckoutAnswer is { Ok: true };
}
