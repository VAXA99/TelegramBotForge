using System.Globalization;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBotForge.Controllers;
using TelegramBotForge.Localization;
using TelegramBotForge.Routing;

namespace TelegramBotForge.Hosting;

/// <summary>
/// Puts the commands with a description into Telegram's <c>/</c> menus, enabled by <c>RegisterCommands()</c>.
/// </summary>
internal sealed class BotCommandRegistrar
{
    private readonly ITelegramBotClient _bot;
    private readonly ActionRegistry _registry;
    private readonly BotLocalizationOptions? _localization;
    private readonly ILogger _logger;

    public BotCommandRegistrar(ITelegramBotClient bot, ActionRegistry registry, BotLocalizationOptions? localization, ILogger logger)
    {
        _bot = bot;
        _registry = registry;
        _localization = localization;
        _logger = logger;
    }

    /// <summary>
    /// A command of the menu and where it is shown.
    /// </summary>
    internal sealed record MenuCommand(string Name, string Description, bool InPrivate, bool InGroups, bool Everywhere);

    /// <summary>
    /// The menus to set: one per scope that has commands, in each language.
    /// </summary>
    internal sealed record Menu(BotCommandScope Scope, string? LanguageCode, IReadOnlyList<BotCommand> Commands);

    public async Task RegisterAsync(CancellationToken ct)
    {
        var commands = Collect(_registry);
        var menus = Plan(commands, _localization);

        try
        {
            foreach (var menu in menus.Set)
            {
                await _bot.SetMyCommands(menu.Commands, menu.Scope, menu.LanguageCode, cancellationToken: ct);
            }

            foreach (var menu in menus.Delete)
                await _bot.DeleteMyCommands(menu.Scope, menu.LanguageCode, cancellationToken: ct);

            _logger.LogInformation("Registered {CommandCount} commands in the menu: {Commands}",
                commands.Count, string.Join(", ", commands.Select(command => "/" + command.Name)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not register the commands in Telegram's menu");
        }
    }

    internal static List<MenuCommand> Collect(ActionRegistry registry)
    {
        var commands = new List<MenuCommand>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Registered in the order the controllers and their actions are declared.
        foreach (var action in registry.Actions.OrderBy(action => action.ControllerType.FullName, StringComparer.Ordinal)
                     .ThenBy(action => action.Method.MetadataToken))
        {
            var chatTypes = action.Constraints.OfType<ChatTypeAttribute>().ToList();
            var types = chatTypes.Count == 0
                ? null
                : chatTypes.Select(attribute => attribute.Types.ToHashSet()).Aggregate((all, next) => { all.IntersectWith(next); return all; });

            foreach (var command in action.Routes.OfType<CommandAttribute>())
            {
                if (command.Description is null || !seen.Add(command.Name))
                    continue;

                commands.Add(new MenuCommand(
                    command.Name.ToLowerInvariant(),
                    command.Description,
                    InPrivate: types is null || types.Contains(ChatType.Private),
                    InGroups: types is null || types.Contains(ChatType.Group) || types.Contains(ChatType.Supergroup),
                    Everywhere: types is null));
            }
        }

        return commands;
    }

    /// <summary>
    /// Telegram shows the most specific menu that exists, without merging: a private chat with its own menu does not see
    /// the default one. So the private and group menus repeat the commands that work everywhere, and a menu with nothing
    /// of its own is deleted, so an old one does not stay.
    /// </summary>
    internal static (List<Menu> Set, List<Menu> Delete) Plan(IReadOnlyList<MenuCommand> commands, BotLocalizationOptions? localization)
    {
        var languages = new List<(string? Code, CultureInfo? Culture)> { (null, localization?.Default) };
        if (localization?.TranslateCommand is not null)
        {
            languages.AddRange(localization.Supported
                .Select(culture => (Code: (string?)culture.TwoLetterISOLanguageName, Culture: (CultureInfo?)culture))
                .DistinctBy(language => language.Code));
        }

        var scopes = new (BotCommandScope Scope, Func<MenuCommand, bool> Includes, bool Own)[]
        {
            (new BotCommandScopeDefault(), command => command.Everywhere, true),
            (new BotCommandScopeAllPrivateChats(), command => command.InPrivate, commands.Any(command => command.InPrivate && !command.Everywhere)),
            (new BotCommandScopeAllGroupChats(), command => command.InGroups, commands.Any(command => command.InGroups && !command.Everywhere))
        };

        var set = new List<Menu>();
        var delete = new List<Menu>();
        foreach (var (code, culture) in languages)
        {
            foreach (var (scope, includes, own) in scopes)
            {
                if (!own)
                {
                    delete.Add(new Menu(scope, code, []));
                    continue;
                }

                var list = commands.Where(includes)
                    .Select(command => new BotCommand(command.Name, Translate(command.Description, culture, localization)))
                    .ToList();
                set.Add(new Menu(scope, code, list));
            }
        }

        return (set, delete);
    }

    private static string Translate(string description, CultureInfo? culture, BotLocalizationOptions? localization) =>
        culture is not null && localization?.TranslateCommand?.Invoke(description, culture) is { Length: > 0 } text ? text : description;
}
