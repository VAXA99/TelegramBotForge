namespace TelegramBotForge.Logging;

/// <summary>
/// Remembers which action processed the current update, so the logging middleware can log it in one line.
/// The trace flows with the asynchronous call, so the router and middleware can mark it without extra parameters.
/// </summary>
internal sealed class UpdateTrace
{
    private static readonly AsyncLocal<UpdateTrace?> CurrentTrace = new();

    /// <summary>
    /// Gets the name of the action that processed the update, or <c>null</c> if nothing processed it.
    /// </summary>
    public string? HandledBy { get; private set; }

    /// <summary>
    /// Gets the conversation state the user was in when the update arrived, if any.
    /// </summary>
    public string? State { get; private set; }

    /// <summary>
    /// Gets a value indicating whether processing the update failed.
    /// </summary>
    public bool Failed { get; private set; }

    /// <summary>
    /// Gets the trace of the update processed by the current asynchronous call, if any.
    /// </summary>
    public static UpdateTrace? Current => CurrentTrace.Value;

    /// <summary>
    /// Describes the action for logs, for example <c>RegistrationController.OnAge (state Registration.Age)</c>.
    /// </summary>
    public string Describe()
    {
        var handler = HandledBy ?? "no action";
        if (State is not null)
            handler = $"{handler} (state {State})";
        return Failed ? handler + " failed" : handler;
    }

    /// <summary>
    /// Records that processing the current update failed.
    /// </summary>
    public static void MarkFailed()
    {
        if (CurrentTrace.Value is { } trace)
            trace.Failed = true;
    }

    /// <summary>
    /// Records the conversation state of the current update.
    /// </summary>
    public static void SetState(string state)
    {
        if (CurrentTrace.Value is { } trace)
            trace.State = state;
    }

    /// <summary>
    /// Starts a trace for the update processed by the current asynchronous call.
    /// </summary>
    public static UpdateTrace Begin()
    {
        var trace = new UpdateTrace();
        CurrentTrace.Value = trace;
        return trace;
    }

    /// <summary>
    /// Records the action of the current update. The first mark wins.
    /// </summary>
    public static void Mark(string handledBy)
    {
        if (CurrentTrace.Value is { HandledBy: null } trace)
            trace.HandledBy = handledBy;
    }

    /// <summary>
    /// Records the class that processed the current update. The first mark wins.
    /// </summary>
    public static void Mark(Type handlerType) => Mark(handlerType.Name);
}
