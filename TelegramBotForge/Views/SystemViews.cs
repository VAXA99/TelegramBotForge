using System.Net;
using System.Text.RegularExpressions;
using TelegramBotForge.Configurations;

namespace TelegramBotForge.Views;

/// <summary>
/// The base of the messages the library itself shows: unknown command, error, rate limit, <c>/paysupport</c>
/// and declined payment. Each one shows its configured text by default.
/// To redefine one, derive from it and override <c>Build</c>; the library finds your class in your project and uses it instead.
/// A view that adds nothing sends nothing.
/// </summary>
/// <example>
/// <code>
/// public sealed class MyUnknownCommandView : UnknownCommandView
/// {
///     protected override void Build(MessageBuilder message) => message
///         .Line($"🤔 I don't know that one.")
///         .Row(Button("📋 Menu", "menu"));
/// }
/// </code>
/// </example>
public abstract class SystemView : View
{
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// The built-in messages, each replaceable by deriving from it.
    /// </summary>
    internal static readonly Type[] All =
    [
        typeof(UnknownCommandView),
        typeof(ErrorView),
        typeof(RateLimitView),
        typeof(PaySupportView),
        typeof(PaymentDeclinedView)
    ];

    /// <summary>
    /// Gets the bot configuration, where the default texts come from.
    /// </summary>
    protected TelegramBotConfiguration Configuration { get; private set; } = new();

    internal void Configure(TelegramBotConfiguration configuration) => Configuration = configuration;

    /// <summary>
    /// Renders the message, or returns <c>null</c> when the view adds nothing.
    /// </summary>
    internal BotMessage? RenderOrNull()
    {
        var message = CreateBuilder();
        Build(message);
        return message.IsEmpty ? null : message.Build();
    }

    /// <summary>
    /// Renders the message as plain text, for places where Telegram allows nothing else.
    /// </summary>
    /// <exception cref="InvalidOperationException">The view has buttons or media.</exception>
    internal string? RenderPlainText()
    {
        if (RenderOrNull() is not { } message)
            return null;

        if (message.Keyboard is not null || message.Media is not null || message.Rich)
        {
            throw new InvalidOperationException(
                $"View {GetType().Name}: Telegram shows this message as plain text, so it cannot have buttons or media or be a rich message.");
        }

        return WebUtility.HtmlDecode(Tags.Replace(message.Text, string.Empty));
    }

    /// <summary>
    /// Adds a configured text as a line, unless it is empty.
    /// </summary>
    private protected static void AddText(MessageBuilder message, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            message.Text(text);
    }
}

/// <summary>
/// Shown when a text message matches no action. By default <c>UnknownCommandMessage</c> ("Unknown command").
/// </summary>
public class UnknownCommandView : SystemView
{
    /// <inheritdoc/>
    protected override void Build(MessageBuilder message) => AddText(message, Configuration.UnknownCommandMessage);
}

/// <summary>
/// Shown when an action fails. By default <c>FallbackErrorMessage</c> ("An error occurred").
/// </summary>
public class ErrorView : SystemView
{
    /// <inheritdoc/>
    protected override void Build(MessageBuilder message) => AddText(message, Configuration.FallbackErrorMessage);
}

/// <summary>
/// Shown once when a user sends too much. By default <c>RateLimit:Message</c>.
/// After a button press it appears as a short notification, so it must be plain text.
/// </summary>
public class RateLimitView : SystemView
{
    /// <inheritdoc/>
    protected override void Build(MessageBuilder message) => AddText(message, Configuration.RateLimit?.Message);
}

/// <summary>
/// The answer to <c>/paysupport</c>, which Telegram requires from bots that sell with Stars. By default <c>Stars:PaySupportMessage</c>.
/// </summary>
public class PaySupportView : SystemView
{
    /// <inheritdoc/>
    protected override void Build(MessageBuilder message) => AddText(message, Configuration.Stars?.PaySupportMessage);
}

/// <summary>
/// Shown in Telegram's payment form when an order is declined by the built-in checks or takes too long.
/// By default <c>Stars:DeclineMessage</c>. Telegram only shows plain text there.
/// </summary>
public class PaymentDeclinedView : SystemView
{
    /// <inheritdoc/>
    protected override void Build(MessageBuilder message) => AddText(message, Configuration.Stars?.DeclineMessage);
}
