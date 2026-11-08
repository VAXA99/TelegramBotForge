using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramBotForge.Results;

/// <summary>
/// Answers an inline query with results the user can pick, for <c>[InlineQuery]</c> actions.
/// </summary>
/// <param name="Results">Up to 50 results, for example made with <see cref="InlineResults"/>. Empty shows "no results".</param>
/// <param name="CacheSeconds">How long Telegram may show the same results for the same query without asking the bot again.</param>
/// <param name="IsPersonal">The results depend on the user, so Telegram caches them per user.</param>
/// <param name="NextOffset">Sent back as <c>InlineQuery.Offset</c> when the user scrolls to the end, to load the next page. <c>null</c> for no more.</param>
/// <param name="Button">A button above the results, for example to open the bot's private chat.</param>
public sealed record AnswerInline(
    IReadOnlyList<InlineQueryResult> Results,
    int CacheSeconds = 300,
    bool IsPersonal = false,
    string? NextOffset = null,
    InlineQueryResultsButton? Button = null) : IBotResult;

/// <summary>
/// Builds the most common inline results.
/// </summary>
public static class InlineResults
{
    /// <summary>
    /// A result that sends a text message when picked.
    /// </summary>
    /// <param name="id">A unique id of the result, up to 64 bytes.</param>
    /// <param name="title">The title in the list of results.</param>
    /// <param name="text">The message that is sent, HTML by default.</param>
    /// <param name="description">A line under the title.</param>
    /// <param name="keyboard">Buttons under the sent message.</param>
    /// <param name="thumbnailUrl">A picture next to the title.</param>
    /// <param name="parseMode">How <paramref name="text"/> is formatted.</param>
    public static InlineQueryResultArticle Article(
        string id,
        string title,
        string text,
        string? description = null,
        InlineKeyboardMarkup? keyboard = null,
        string? thumbnailUrl = null,
        ParseMode parseMode = ParseMode.Html)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentException.ThrowIfNullOrEmpty(text);
        return new InlineQueryResultArticle(id, title, new InputTextMessageContent(text) { ParseMode = parseMode })
        {
            Description = description,
            ReplyMarkup = keyboard,
            ThumbnailUrl = thumbnailUrl
        };
    }
}
