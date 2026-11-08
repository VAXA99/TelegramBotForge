using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TelegramBotForge.Middleware;
using TelegramBotForge.Results;

namespace TelegramBotForge.Localization;

/// <summary>
/// Sets the culture of the update to the user's language, added by <c>UseLocalization()</c>.
/// </summary>
internal sealed class LocalizationMiddleware : IBotMiddleware
{
    private readonly BotLocalizationOptions _options;

    public LocalizationMiddleware(BotLocalizationOptions options) => _options = options;

    public async Task<IBotResult?> InvokeAsync(BotContext context, BotNext next, CancellationToken ct)
    {
        var resolver = context.Services.GetRequiredService<IBotCultureResolver>();
        var culture = _options.Match(await resolver.GetLanguageAsync(context, ct))
                      ?? _options.Match(context.From?.LanguageCode)
                      ?? _options.Default;

        context.Culture = culture;

        var previousUi = CultureInfo.CurrentUICulture;
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentUICulture = culture;
        if (_options.SetCurrentCulture)
            CultureInfo.CurrentCulture = culture;

        try
        {
            return await next(context, ct);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previous;
        }
    }
}
