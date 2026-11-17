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
        context.SetCurrentCulture = _options.SetCurrentCulture;
        using var cultureScope = new BotCultureScope(culture, context.SetCurrentCulture);
        return await next(context, ct);
    }
}

internal sealed class BotCultureScope : IDisposable
{
    private readonly CultureInfo _previous = CultureInfo.CurrentCulture;
    private readonly CultureInfo _previousUi = CultureInfo.CurrentUICulture;

    public BotCultureScope(CultureInfo? culture, bool setCurrentCulture)
    {
        Apply(culture, setCurrentCulture);
    }

    public void Apply(CultureInfo? culture, bool setCurrentCulture)
    {
        if (culture is null)
            return;
        CultureInfo.CurrentUICulture = culture;
        if (setCurrentCulture)
            CultureInfo.CurrentCulture = culture;
    }

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _previousUi;
        CultureInfo.CurrentCulture = _previous;
    }
}
