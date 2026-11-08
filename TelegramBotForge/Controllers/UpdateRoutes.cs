using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace TelegramBotForge.Controllers;

/// <summary>
/// Makes the method an action for a contact the user shares, bound to a <c>Contact</c> parameter (Telegram's own type).
/// It usually answers a reply-keyboard button made with <c>KeyboardButton.WithRequestContact</c>.
/// </summary>
/// <example>
/// <code>
/// [Contact(OwnOnly = true)]
/// public async Task&lt;IBotResult&gt; Phone(Contact contact)
/// {
///     await users.SetPhoneAsync(Context.UserId!.Value, contact.PhoneNumber);
///     return Reply("Thanks, your number is saved.", new ReplyKeyboardRemove());
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ContactAttribute : RouteAttribute
{
    /// <summary>
    /// Gets or sets a value indicating whether only the user's own contact matches, as sent by a "share my phone number"
    /// button, and not a contact of someone else forwarded from the address book.
    /// </summary>
    public bool OwnOnly { get; set; }

    /// <inheritdoc/>
    public override int Priority => 20;

    /// <inheritdoc/>
    public override string Key => OwnOnly ? "own contact" : "contact";

    /// <inheritdoc/>
    public override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.Contact is { } contact && (!OwnOnly || (contact.UserId is { } id && id == context.UserId));
}

/// <summary>
/// Makes the method an action for a message the user edited, with the new version bound to a <c>Message</c> parameter
/// and its text to <c>string text</c>. Without such an action, edits are ignored.
/// </summary>
/// <remarks>
/// <c>[Text]</c>, <c>[Command]</c> and the other message routes never match edits, so editing <c>/start</c> does not run it again.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EditedAttribute : RouteAttribute
{
    /// <inheritdoc/>
    public override int Priority => 10;

    /// <inheritdoc/>
    public override string Key => "edited message";

    /// <inheritdoc/>
    public override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.EditedMessage is not null;
}

/// <summary>
/// Makes the method an action for a text message the user edited, with the new text bound to <c>string text</c>.
/// Tried before <see cref="EditedAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EditedTextAttribute : RouteAttribute
{
    /// <inheritdoc/>
    public override int Priority => 15;

    /// <inheritdoc/>
    public override string Key => "edited text";

    /// <inheritdoc/>
    public override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.EditedMessage?.Text is not null;
}

/// <summary>
/// The base of the routes for changes of the bot's own membership in a chat (Telegram's <c>my_chat_member</c> update).
/// The change is bound to a <c>ChatMemberUpdated</c> parameter.
/// </summary>
public abstract class MembershipRouteAttribute : RouteAttribute
{
    /// <inheritdoc/>
    public override int Priority => 100;

    /// <inheritdoc/>
    public override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.MembershipChange is { } change && IsMatch(change);

    /// <summary>
    /// Checks the change.
    /// </summary>
    protected abstract bool IsMatch(ChatMemberUpdated change);

    private protected static bool IsIn(ChatMember member) =>
        member.Status is ChatMemberStatus.Member or ChatMemberStatus.Administrator or ChatMemberStatus.Creator ||
        member is ChatMemberRestricted { IsMember: true };

    private protected static bool IsPrivate(ChatMemberUpdated change) => change.Chat.Type == ChatType.Private;
}

/// <summary>
/// Makes the method an action for a user who blocked the bot (stopped it in the private chat).
/// Nothing can be sent to the user anymore, so the action returns nothing; mark the user as inactive, for example.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BotBlockedAttribute : MembershipRouteAttribute
{
    /// <inheritdoc/>
    public override string Key => "bot blocked";

    /// <inheritdoc/>
    protected override bool IsMatch(ChatMemberUpdated change) =>
        IsPrivate(change) && change.NewChatMember.Status == ChatMemberStatus.Kicked;
}

/// <summary>
/// Makes the method an action for a user who unblocked the bot (restarted it in the private chat).
/// Telegram usually sends <c>/start</c> right after it, so a greeting belongs there.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class BotUnblockedAttribute : MembershipRouteAttribute
{
    /// <inheritdoc/>
    public override string Key => "bot unblocked";

    /// <inheritdoc/>
    protected override bool IsMatch(ChatMemberUpdated change) =>
        IsPrivate(change) && change.OldChatMember.Status == ChatMemberStatus.Kicked && IsIn(change.NewChatMember);
}

/// <summary>
/// Makes the method an action for the bot being added to a group or channel. The user who added it is <c>Context.From</c>.
/// A result is sent to that chat, for example a greeting with what the bot can do.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AddedToChatAttribute : MembershipRouteAttribute
{
    /// <inheritdoc/>
    public override string Key => "added to chat";

    /// <inheritdoc/>
    protected override bool IsMatch(ChatMemberUpdated change) =>
        !IsPrivate(change) && !IsIn(change.OldChatMember) && IsIn(change.NewChatMember);
}

/// <summary>
/// Makes the method an action for the bot being removed from a group or channel, or the group being deleted.
/// Nothing can be sent to that chat anymore.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RemovedFromChatAttribute : MembershipRouteAttribute
{
    /// <inheritdoc/>
    public override string Key => "removed from chat";

    /// <inheritdoc/>
    protected override bool IsMatch(ChatMemberUpdated change) =>
        !IsPrivate(change) && IsIn(change.OldChatMember) && !IsIn(change.NewChatMember);
}

/// <summary>
/// Makes the method an action for an inline query: a user typing <c>@YourBot something</c> in any chat.
/// The query is bound to a <c>string query</c> parameter (or <c>InlineQuery</c>), and the action answers with
/// <c>AnswerInline(...)</c>. Turn on inline mode for the bot with <c>/setinline</c> in @BotFather first.
/// </summary>
/// <remarks>
/// Inline queries have no chat, so <c>[State]</c> does not apply to them and <c>Context.ChatId</c> is <c>null</c>.
/// </remarks>
/// <example>
/// <code>
/// [InlineQuery]
/// public async Task&lt;IBotResult&gt; Search(string query)
/// {
///     var products = await shop.SearchAsync(query);
///     return AnswerInline(products.Select(p => InlineResults.Article(p.Id.ToString(), p.Name, $"&lt;b&gt;{p.Name}&lt;/b&gt; {p.Price}⭐")));
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class InlineQueryAttribute : RouteAttribute
{
    /// <inheritdoc/>
    public override int Priority => 100;

    /// <inheritdoc/>
    public override string Key => "inline query";

    /// <inheritdoc/>
    public override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.InlineQuery is not null;
}

/// <summary>
/// Makes the method an action for an album: several photos or videos sent together. Telegram sends each item as its own
/// update; with an <c>[Album]</c> action in the bot, the items are collected and the action runs once with all of them,
/// bound to an <c>IReadOnlyList&lt;BotMedia&gt;</c> (or <c>BotMedia[]</c>) parameter. The caption is on one of the items.
/// </summary>
/// <remarks>
/// Items count as one album when they arrive less than <c>AlbumWaitMilliseconds</c> (500 by default) apart.
/// With an <c>[Album]</c> action, <c>[Media]</c> actions no longer see album items one by one: an album that no
/// <c>[Album]</c> action takes (for example because of <c>[State]</c>) reaches them as its first item.
/// Collecting needs every item to reach the same instance, and <c>ParallelPerQueue</c> to be 1.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AlbumAttribute : RouteAttribute
{
    /// <inheritdoc/>
    public override int Priority => 40;

    /// <inheritdoc/>
    public override string Key => "album";

    /// <inheritdoc/>
    public override bool TryMatch(BotContext context, IDictionary<string, string> values) =>
        context.Album is { Count: > 0 };
}
