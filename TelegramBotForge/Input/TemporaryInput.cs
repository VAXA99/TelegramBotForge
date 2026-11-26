using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Results;
using TelegramBotForge.State;
using Message = TelegramBotForge.Views.Message;

namespace TelegramBotForge.Input;

/// <summary>Supported response correlation: Telegram request ID for users; reply-to-prompt for locations.</summary>
public enum TemporaryInputKind { Users, Location }
/// <summary>Whether a command addressed to this bot closes the current request before routing.</summary>
public enum TemporaryInputCommandPolicy { Preserve, Cancel }

/// <summary>Expiry and application-supplied keyboard-removal notice. Commands preserve the request by default.</summary>
public sealed class TemporaryInputOptions
{
    public TemporaryInputOptions(TimeSpan lifetime, string cleanupText, TemporaryInputCommandPolicy commands = TemporaryInputCommandPolicy.Preserve)
    {
        ResultLifecycleExtensions.ValidateDelay(lifetime);
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        ArgumentException.ThrowIfNullOrWhiteSpace(cleanupText);
        if (!Enum.IsDefined(commands))
            throw new ArgumentOutOfRangeException(nameof(commands));
        Lifetime = lifetime;
        CleanupText = cleanupText;
        Commands = commands;
    }

    public TimeSpan Lifetime { get; }
    public string CleanupText { get; }
    public TemporaryInputCommandPolicy Commands { get; }
}

/// <summary>An immutable snapshot. Prompt is null until confirmed delivery; Id identifies this opening, not a business entity.</summary>
public sealed class TemporaryInputRequest
{
    internal TemporaryInputRequest(InputSession session)
    {
        Id = session.Id;
        ChatId = session.ChatId;
        UserId = session.UserId;
        State = session.State;
        PreviousState = session.PreviousState;
        Kind = session.Kind;
        RequestId = session.RequestId;
        ExpiresAt = session.ExpiresAt;
        Prompt = session.Prompt;
    }

    public Guid Id { get; }
    public long ChatId { get; }
    public long UserId { get; }
    public string State { get; }
    public string? PreviousState { get; }
    public TemporaryInputKind Kind { get; }
    public int? RequestId { get; }
    public DateTimeOffset ExpiresAt { get; }
    public MessageDelivery? Prompt { get; }
}

/// <summary>A scoped, state-backed temporary conversation. Location responses must reply to the current prompt.</summary>
public interface ITemporaryInput
{
    /// <summary>The request owned by this chat/user and current state, including a pending request. TryMatch also checks expiry.</summary>
    TemporaryInputRequest? Current { get; }
    /// <summary>Saves the previous state and returns a receipt-tracked prompt. Rejects a second active request; does not clear scenario data.</summary>
    IBotResult Begin(object state, TemporaryInputKind kind, Func<TemporaryInputRequest, Message> prompt, TemporaryInputOptions options);
    /// <summary>Checks ownership, confirmed delivery, expiry and response correlation without running business validation.</summary>
    bool TryMatch([NotNullWhen(true)] out TemporaryInputRequest? request);
    /// <summary>Closes a matching response exactly once and restores the previous state before best-effort cleanup.</summary>
    IBotResult Complete(Guid requestId, string text);
    /// <summary>Closes the owned request without requiring a response. A stale ID is a no-op, including keyboard cleanup.</summary>
    IBotResult Cancel(Guid requestId, string text);
}

internal sealed record InputSession(Guid Id, long ChatId, long UserId, string State, string? PreviousState,
    TemporaryInputKind Kind, int? RequestId, DateTimeOffset ExpiresAt, TimeSpan Lifetime, string CleanupText,
    TemporaryInputCommandPolicy Commands, MessageDelivery? Prompt = null);

internal sealed class TemporaryInput(BotContext context, TimeProvider time, TelegramBotBuilder builder, ILogger<TemporaryInput> logger) : ITemporaryInput
{
    internal const string SessionKey = "TelegramBotForge.Input.Active";
    private const string SequenceKey = "TelegramBotForge.Input.Sequence";

    public TemporaryInputRequest? Current => Read() is { } session && Owns(session) ? new(session) : null;

    public IBotResult Begin(object state, TemporaryInputKind kind, Func<TemporaryInputRequest, Message> prompt, TemporaryInputOptions options)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        var conversation = RequireState();
        if (options.Lifetime > builder.StateTimeToLive)
            throw new ArgumentException("Temporary input lifetime must not exceed the configured conversation state TTL.", nameof(options));
        if (Read() is not null)
            throw new InvalidOperationException("A temporary input request already exists. Complete or cancel it before starting another one.");
        var name = StateNames.Of(state);
        int? telegramId = null;
        if (kind == TemporaryInputKind.Users)
        {
            var sequence = conversation.Get<int?>(SequenceKey) ?? RandomNumberGenerator.GetInt32(1, int.MaxValue / 2);
            if (sequence == int.MaxValue)
                throw new InvalidOperationException("The users-selection request ID sequence is exhausted.");
            telegramId = sequence + 1;
            conversation.Set(SequenceKey, telegramId.Value);
        }
        var session = new InputSession(Guid.NewGuid(), context.ChatId!.Value, context.UserId!.Value,
            name, conversation.Current, kind, telegramId, time.GetUtcNow() + options.Lifetime, options.Lifetime,
            options.CleanupText, options.Commands);
        conversation.Set(SessionKey, session);
        conversation.Current = name;
        try
        {
            var message = prompt(new(session)) ?? throw new InvalidOperationException("The temporary input prompt factory returned null.");
            var delivered = message.Delivered;
            message = message.OnDelivered((receipt, token) =>
            {
                Confirm(receipt);
                return delivered?.Invoke(receipt, token) ?? Task.CompletedTask;
            });
            return new DeliverMessage(message).DeleteAfter(options.Lifetime).OnDelivered((receipt, _) =>
            {
                Confirm(receipt);
                return Task.CompletedTask;
            }).Finally(() =>
            {
                if (Read() is { Prompt: null } current && current.Id == session.Id)
                    Restore(current);
            });
        }
        catch
        {
            Restore(session);
            throw;
        }

        void Confirm(MessageDelivery receipt)
        {
            if (Read() is not { Prompt: null } current || current.Id != session.Id || !Owns(current))
                return;
            if (receipt.ChatId != session.ChatId)
                throw new InvalidOperationException("The input prompt was delivered to another chat.");
            conversation.Set(SessionKey, current with { Prompt = receipt, ExpiresAt = time.GetUtcNow() + current.Lifetime });
        }
    }

    public bool TryMatch([NotNullWhen(true)] out TemporaryInputRequest? request)
    {
        request = null;
        if (Read() is not { Prompt: { } prompt } session || !Owns(session) || session.ExpiresAt <= time.GetUtcNow())
            return false;
        var matches = session.Kind switch
        {
            TemporaryInputKind.Users => context.UsersShared is { } selection && selection.RequestId == session.RequestId,
            TemporaryInputKind.Location => context.Location is not null && context.Message?.ReplyToMessage?.Id == prompt.PrimaryMessageId,
            _ => false
        };
        if (!matches)
            return false;
        request = new(session);
        return true;
    }

    public IBotResult Complete(Guid requestId, string text)
    {
        if (!TryMatch(out var request) || request.Id != requestId)
            return new MultiResult();
        return Close(requestId, text);
    }

    public IBotResult Cancel(Guid requestId, string text) => Close(requestId, text);

    internal IBotResult? BeforeRouting()
    {
        if (Read() is not { } session)
            return null;
        if (session.ChatId != context.ChatId || session.UserId != context.UserId)
        {
            RequireState().Remove(SessionKey);
            return null;
        }
        if (!Owns(session))
        {
            RequireState().Remove(SessionKey);
            return Cleanup(session, null);
        }
        if (session.Prompt is null || session.ExpiresAt <= time.GetUtcNow() ||
            session.Commands == TemporaryInputCommandPolicy.Cancel && context.Command is not null && !context.IsCommandForAnotherBot)
        {
            Restore(session);
            return session.Prompt is null ? null : Cleanup(session, session.CleanupText);
        }
        return null;
    }

    private IBotResult Close(Guid requestId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (Read() is not { } session || session.Id != requestId || !Owns(session))
            return new MultiResult();
        Restore(session);
        return session.Prompt is null ? new MultiResult() : Cleanup(session, text);
    }

    private IBotResult Cleanup(InputSession session, string? text) => new MultiResult().Finally(async () =>
    {
        if (session.Prompt is { } receipt)
            foreach (var messageId in receipt.AssociatedMessageIds.Prepend(receipt.PrimaryMessageId).Distinct())
            {
                try { await MessageDeletionScheduler.DeleteAsync(context.Bot, session.ChatId, messageId, CancellationToken.None); }
                catch (Exception exception) { logger.LogWarning(exception, "Could not delete temporary input {RequestId} message {MessageId}", session.Id, messageId); }
            }
        if (text is null)
            return;
        try
        {
            var executor = context.Services.GetRequiredService<IBotResultExecutor>();
            await ResultLifecycleExtensions.ExecuteAsync(context, new SendText(WebUtility.HtmlEncode(text), new ReplyKeyboardRemove()), executor.ExecuteAsync, CancellationToken.None);
        }
        catch (Exception exception) { logger.LogWarning(exception, "Could not remove the keyboard for closed temporary input {RequestId}", session.Id); }
    });

    private InputSession? Read() => context.State?.Get<InputSession>(SessionKey);
    private bool Owns(InputSession session) => session.ChatId == context.ChatId && session.UserId == context.UserId && session.State == context.State?.Current;
    private ChatState RequireState() => context.ChatId is not null && context.UserId is not null && context.State is { } state
        ? state : throw new InvalidOperationException("Temporary input requires a user update with loaded conversation state.");

    private void Restore(InputSession session)
    {
        var state = RequireState();
        if (Read()?.Id != session.Id)
            return;
        state.Remove(SessionKey);
        if (state.Current == session.State)
            state.Current = session.PreviousState;
    }
}
