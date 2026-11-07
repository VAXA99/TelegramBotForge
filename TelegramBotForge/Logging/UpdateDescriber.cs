using Telegram.Bot.Types;
using TelegramBotForge.Results;

namespace TelegramBotForge.Logging;

/// <summary>
/// Describes Telegram updates for log lines, for example <c>text "/start"</c> and <c>@user (42)</c>.
/// </summary>
internal static class UpdateDescriber
{
    private const int MaxTextLength = 60;

    public static string Describe(Update update, bool includeText)
    {
        if (update.CallbackQuery is { } callback)
            return callback.Data is { } data ? $"button {Quote(data, includeText)}" : "button";
        if (update.PreCheckoutQuery is { } checkout)
            return $"pre-checkout {checkout.TotalAmount} {checkout.Currency}";
        if (update.MyChatMember is { } change)
            return $"membership {change.OldChatMember.Status} → {change.NewChatMember.Status}".ToLowerInvariant();

        var message = update.Message ?? update.EditedMessage;
        if (message is null)
            return update.Type.ToString().ToLowerInvariant();

        if (message.SuccessfulPayment is { } paid)
            return $"payment {paid.TotalAmount} {paid.Currency}";
        if (message.Text is { } text)
            return $"{(update.EditedMessage is not null ? "edited text" : "text")} {Quote(text, includeText)}";
        if (message.Contact is not null)
            return "shared contact";
        if (message.Location is not null)
            return "location";

        var type = message.Type.ToString().ToLowerInvariant();
        return string.IsNullOrEmpty(message.Caption) ? type : $"{type} {Quote(message.Caption, includeText)}";
    }

    public static string DescribeSender(Update update)
    {
        if (GetSender(update) is not { } user)
            return "unknown sender";

        var name = !string.IsNullOrEmpty(user.Username) ? "@" + user.Username : user.FirstName;
        var sender = user.IsBot ? $"{name} ({user.Id}, bot)" : $"{name} ({user.Id})";
        return GetChat(update) is { } chat && chat.Id != user.Id
            ? $"{sender} in chat {chat.Id}"
            : sender;
    }

    public static string DescribeResult(IBotResult? result) => result switch
    {
        null => "no answer",
        MultiResult multi => string.Join(" + ", multi.Results.Select(DescribeResult)),
        _ => result.GetType().Name
    };

    /// <summary>Gets the user who caused the update, if the update type has one.</summary>
    public static User? GetSender(Update update) =>
        update.Message?.From
        ?? update.EditedMessage?.From
        ?? update.CallbackQuery?.From
        ?? update.MyChatMember?.From
        ?? update.InlineQuery?.From
        ?? update.PreCheckoutQuery?.From;

    /// <summary>Gets the chat the update happened in, if the update type has one.</summary>
    public static Chat? GetChat(Update update) =>
        update.Message?.Chat
        ?? update.EditedMessage?.Chat
        ?? update.CallbackQuery?.Message?.Chat
        ?? update.MyChatMember?.Chat;

    private static string Quote(string text, bool includeText)
    {
        if (!includeText)
            return $"({text.Length} chars)";

        var oneLine = text.ReplaceLineEndings(" ⏎ ");
        return oneLine.Length <= MaxTextLength
            ? $"\"{oneLine}\""
            : $"\"{oneLine[..(MaxTextLength - 1)]}…\"";
    }
}
