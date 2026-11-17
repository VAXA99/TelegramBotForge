using Telegram.Bot;
using TelegramBotForge.Hosting;

namespace TelegramBotForge.DeepLinks;

/// <summary>Builds Telegram start links using the connected bot's username.</summary>
public interface IBotDeepLinks
{
    Task<string> CreateAsync(string payload, bool group = false, CancellationToken ct = default);
}

internal sealed class TelegramBotDeepLinks(ITelegramBotClient bot, BotIdentity identity) : IBotDeepLinks
{
    public Task<string> CreateAsync(string payload, bool group = false, CancellationToken ct = default)
    {
        DeepLinkPayload.Validate(payload);
        return BuildAsync(payload, group, ct);
    }

    private async Task<string> BuildAsync(string payload, bool group, CancellationToken ct)
    {
        var username = identity.Username;
        if (string.IsNullOrEmpty(username))
        {
            username = (await bot.GetMe(ct)).Username;
            if (string.IsNullOrEmpty(username))
                throw new InvalidOperationException("Telegram did not return a username for this bot.");
            identity.Username = username;
        }
        return $"https://t.me/{username}?{(group ? "startgroup" : "start")}={payload}";
    }
}

internal static class DeepLinkPayload
{
    internal static bool IsCharacter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-';

    internal static bool IsValid(string payload) => payload.Length is >= 1 and <= 64 && payload.All(IsCharacter);

    internal static void Validate(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!IsValid(payload))
            throw new ArgumentException("Deep-link payload must contain 1-64 characters: A-Z, a-z, 0-9, _ or -.", nameof(payload));
    }
}
