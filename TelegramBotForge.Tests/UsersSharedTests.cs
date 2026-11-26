using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBotForge.Controllers;
using TelegramBotForge.Results;

namespace TelegramBotForge.Tests;

public sealed class UsersSelectionController : BotController
{
    [Command("users_route")]
    public IBotResult Open()
    {
        State.Current = "users-route";
        return Reply("Select");
    }

    [UsersShared, State("users-route"), ChatType(ChatType.Private), Shout]
    public IBotResult Selected(UsersShared selection, CancellationToken ct)
    {
        Assert.Same(Context.Update.Message!.UsersShared, selection);
        Assert.Same(Context.UsersShared, selection);
        Assert.Equal(Context.CancellationToken, ct);
        var user = Assert.Single(selection.Users);
        Assert.Equal("Surname", user.LastName);
        Assert.Equal("username", user.Username);
        Assert.Equal("photo", Assert.Single(user.Photo!).FileId);
        return Reply($"Selected {selection.RequestId}: {user.UserId} {user.FirstName}");
    }
}

public sealed class UsersSharedTests
{
    [Fact]
    public async Task Original_selection_is_bound_with_request_id_state_chat_constraint_and_filter()
    {
        await using var bot = TestBot.Create();
        var user = bot.PrivateChat();
        var selection = Selection();
        Assert.True((await user.SendMessage(message => message.UsersShared = selection)).IsEmpty);
        await user.SendText("/users_route");
        Assert.Equal("SELECTED -17: 123 NAME", (await user.SendMessage(message => message.UsersShared = selection)).Text);

        var group = bot.GroupChat(-100500);
        await group.SendText("/users_route");
        Assert.True((await group.SendMessage(message => message.UsersShared = selection)).IsEmpty);
    }

    [Fact]
    public async Task Other_messages_never_match_users_shared_route()
    {
        await using var bot = TestBot.Create(services: services => services.AddSingleton<Members>());
        var user = bot.PrivateChat();
        await user.SendText("/users_route");
        Assert.Equal("Unknown command", (await user.SendText("ordinary")).Text);
        Assert.Equal("Saved +123", (await user.SendContact("+123")).Text);
        Assert.True((await user.SendLocation(10, 20)).IsEmpty);
        Assert.True((await user.SendMessage(_ => { })).IsEmpty);
        Assert.Equal(20, new UsersSharedAttribute().Priority);
        Assert.Equal(new ContactAttribute().Priority, new UsersSharedAttribute().Priority);
    }

    private static UsersShared Selection() => new()
    {
        RequestId = -17,
        Users = [new SharedUser
        {
            UserId = 123, FirstName = "Name", LastName = "Surname", Username = "username",
            Photo = [new PhotoSize { FileId = "photo", FileUniqueId = "unique", Width = 10, Height = 10 }]
        }]
    };
}
