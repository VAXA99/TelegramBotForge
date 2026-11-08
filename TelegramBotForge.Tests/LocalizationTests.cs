using System.Globalization;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using TelegramBotForge.Localization;
using TelegramBotForge.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace TelegramBotForge.Tests;

public sealed class StoredLanguage : IBotCultureResolver
{
    public ValueTask<string?> GetLanguageAsync(BotContext context, CancellationToken ct) =>
        ValueTask.FromResult<string?>(context.UserId == 7 ? "ka" : null);
}

public sealed class LocalizationTests
{
    private static BotTestHost Create(Action<TelegramBotBuilder>? more = null) => TestBot.Create(bot: b =>
    {
        b.UseLocalization(options =>
        {
            options.DefaultCulture = "en";
            options.SupportedCultures = ["en", "ru", "pt", "ka"];
        });
        more?.Invoke(b);
    });

    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("pt-br", "pt")]
    [InlineData("fr", "en")]
    [InlineData(null, "en")]
    public async Task Telegram_language_is_used_by_default(string? telegramLanguage, string expected)
    {
        await using var bot = Create();
        var result = await bot.PrivateChat(languageCode: telegramLanguage).SendText("/lang");
        Assert.Equal($"{expected}|{expected}", result.Text);
    }

    [Fact]
    public async Task Stored_language_wins_and_unknown_falls_back_to_telegram()
    {
        await using var bot = Create(b => b.UseCultureResolver<StoredLanguage>());
        Assert.Equal("ka|ka", (await bot.PrivateChat(userId: 7, languageCode: "ru").SendText("/lang")).Text);
        Assert.Equal("ru|ru", (await bot.PrivateChat(userId: 8, languageCode: "ru").SendText("/lang")).Text);
    }

    [Fact]
    public async Task Without_localization_the_current_culture_is_kept()
    {
        // A culture the machine is unlikely to have, so the test means the same everywhere.
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            await using var bot = TestBot.Create();
            var result = await bot.PrivateChat(languageCode: "ru").SendText("/lang");
            Assert.Equal("de-DE|de-DE", result.Text);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture);
        }
    }

    [Fact]
    public void Default_must_be_supported() =>
        Assert.Throws<ArgumentException>(() => TestBot.Create(bot: b => b.UseLocalization(o =>
        {
            o.DefaultCulture = "de";
            o.SupportedCultures = ["en"];
        })));
}

public sealed class CommandMenuTests
{
    [Fact]
    public async Task Commands_with_descriptions_are_registered_per_scope()
    {
        await using var bot = TestBot.Create(bot: b => b.RegisterCommands());
        await bot.RegisterCommandsAsync();

        var set = bot.Telegram.RequestsOf<SetMyCommandsRequest>();
        string[] Names(BotCommandScope scope) =>
            set.Single(request => request.Scope?.GetType() == scope.GetType() && request.LanguageCode is null)
                .Commands.Select(command => command.Command).Order().ToArray();

        Assert.Equal(["menu", "start"], Names(new BotCommandScopeDefault()));
        Assert.Equal(["ban", "menu", "start"], Names(new BotCommandScopeAllPrivateChats()));
        Assert.Equal(["menu", "poll", "start"], Names(new BotCommandScopeAllGroupChats()));
        Assert.Empty(bot.Telegram.RequestsOf<DeleteMyCommandsRequest>());
    }

    [Fact]
    public async Task Descriptions_are_translated_for_each_language()
    {
        await using var bot = TestBot.Create(bot: b => b
            .RegisterCommands()
            .UseLocalization(o =>
            {
                o.SupportedCultures = ["en", "ru"];
                o.TranslateCommand = (description, culture) => culture.Name == "ru" ? "RU " + description : null;
            }));
        await bot.RegisterCommandsAsync();

        var russian = bot.Telegram.RequestsOf<SetMyCommandsRequest>()
            .Single(request => request.LanguageCode == "ru" && request.Scope is BotCommandScopeDefault);
        Assert.Contains(russian.Commands, command => command is { Command: "start", Description: "RU Start the bot" });

        var english = bot.Telegram.RequestsOf<SetMyCommandsRequest>()
            .Single(request => request.LanguageCode == "en" && request.Scope is BotCommandScopeDefault);
        Assert.Contains(english.Commands, command => command is { Command: "start", Description: "Start the bot" });
    }
}
