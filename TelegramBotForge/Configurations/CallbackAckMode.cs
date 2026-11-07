namespace TelegramBotForge.Configurations;

/// <summary>
/// Defines when the runtime answers a callback query to clear the inline button's loading state.
/// Set with <see cref="TelegramBotBuilder.AckCallbacks"/>.
/// </summary>
public enum CallbackAckMode
{
    /// <summary>
    /// Answers the button press as soon as the update arrives, before the action runs.
    /// The button feels responsive, but an action cannot show its own notification or alert: <c>Answer(...)</c> is ignored.
    /// </summary>
    Immediate,

    /// <summary>
    /// Leaves answering to the action, which returns <c>Answer(...)</c>. Buttons the action does not answer are answered
    /// without a notification after it ran. Use it when actions show notifications or alerts.
    /// </summary>
    Manual
}
