namespace TelegramBotForge.Localization;

/// <summary>
/// Chooses the language of an update. The default uses the language of the user's Telegram app.
/// Register your own with <c>UseCultureResolver&lt;T&gt;()</c> to use, for example, the language the user picked in your bot.
/// </summary>
/// <example>
/// <code>
/// public sealed class UserLanguageResolver(IUserRepository users) : IBotCultureResolver
/// {
///     public async ValueTask&lt;string?&gt; GetLanguageAsync(BotContext context, CancellationToken ct) =>
///         context.UserId is { } id ? (await users.FindAsync(id, ct))?.Language : null;
/// }
/// </code>
/// </example>
public interface IBotCultureResolver
{
    /// <summary>
    /// Returns the language of the update, such as <c>"ru"</c> or <c>"pt-BR"</c>, or <c>null</c> when you don't know it:
    /// then the language of the user's Telegram app is used, and after it the default language.
    /// </summary>
    ValueTask<string?> GetLanguageAsync(BotContext context, CancellationToken ct);
}

/// <summary>
/// The default: the language of the user's Telegram app.
/// </summary>
internal sealed class TelegramLanguageCultureResolver : IBotCultureResolver
{
    public ValueTask<string?> GetLanguageAsync(BotContext context, CancellationToken ct) =>
        ValueTask.FromResult(context.From?.LanguageCode);
}
