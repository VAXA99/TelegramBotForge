using System.Globalization;

namespace TelegramBotForge.Localization;

/// <summary>
/// The languages of the bot, set with <c>UseLocalization(options => ...)</c>.
/// </summary>
public sealed class BotLocalizationOptions
{
    /// <summary>
    /// Gets or sets the language used when the user's language is unknown or not supported. <c>"en"</c> by default.
    /// </summary>
    public string DefaultCulture { get; set; } = "en";

    /// <summary>
    /// Gets or sets the languages the bot speaks, for example <c>["en", "ru", "ka"]</c>. A user's language that is not
    /// in the list falls back to its parent (<c>pt-br</c> → <c>pt</c>), then to <see cref="DefaultCulture"/>.
    /// When empty, any valid language is used as it is.
    /// </summary>
    public string[] SupportedCultures { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="CultureInfo.CurrentCulture"/> (numbers and dates) is set too,
    /// not only <see cref="CultureInfo.CurrentUICulture"/> (texts). On by default.
    /// </summary>
    public bool SetCurrentCulture { get; set; } = true;

    /// <summary>
    /// Gets or sets how a command description becomes the text of one language, for the <c>/</c> menus registered by
    /// <c>RegisterCommands()</c>. It gets the <c>Description</c> of the <c>[Command]</c> (a text or a resource key) and a
    /// culture from <see cref="SupportedCultures"/>, and returns the translation, or <c>null</c> to keep the description.
    /// </summary>
    /// <example>
    /// <code>options.TranslateCommand = (key, culture) => Resources.Commands.ResourceManager.GetString(key, culture);</code>
    /// </example>
    public Func<string, CultureInfo, string?>? TranslateCommand { get; set; }

    internal CultureInfo Default { get; private set; } = CultureInfo.GetCultureInfo("en");

    internal IReadOnlyList<CultureInfo> Supported { get; private set; } = [];

    internal void Validate()
    {
        Default = Parse(DefaultCulture, nameof(DefaultCulture))
                  ?? throw new ArgumentException($"{nameof(DefaultCulture)} \"{DefaultCulture}\" is not a known culture.");
        Supported = (SupportedCultures ?? [])
            .Select(name => Parse(name, nameof(SupportedCultures))
                            ?? throw new ArgumentException($"\"{name}\" in {nameof(SupportedCultures)} is not a known culture."))
            .Distinct()
            .ToList();

        if (Supported.Count > 0 && !Supported.Contains(Default))
            throw new ArgumentException($"{nameof(DefaultCulture)} \"{DefaultCulture}\" must be one of the {nameof(SupportedCultures)}.");
    }

    /// <summary>
    /// Picks the culture for a language code from Telegram or your application: the exact one, its parent, or <c>null</c>.
    /// </summary>
    internal CultureInfo? Match(string? language)
    {
        var culture = Parse(language, null);
        if (culture is null)
            return null;

        if (Supported.Count == 0)
            return culture;

        for (var current = culture; !Equals(current, CultureInfo.InvariantCulture); current = current.Parent)
        {
            if (Supported.Contains(current))
                return current;
        }

        return null;
    }

    private static CultureInfo? Parse(string? name, string? setting)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        try
        {
            var culture = CultureInfo.GetCultureInfo(name.Trim(), predefinedOnly: true);
            return Equals(culture, CultureInfo.InvariantCulture) ? null : culture;
        }
        catch (CultureNotFoundException)
        {
            return setting is null ? null : throw new ArgumentException($"\"{name}\" in {setting} is not a known culture.");
        }
    }
}
