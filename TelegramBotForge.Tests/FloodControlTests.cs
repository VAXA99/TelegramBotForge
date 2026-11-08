using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Requests;
using TelegramBotForge.Configurations;
using TelegramBotForge.Hosting;
using TelegramBotForge.Testing;

namespace TelegramBotForge.Tests;

public sealed class FloodControlTests
{
    [Theory]
    [InlineData(typeof(SendMessageRequest), true)]
    [InlineData(typeof(SendPhotoRequest), true)]
    [InlineData(typeof(CopyMessageRequest), true)]
    [InlineData(typeof(ForwardMessagesRequest), true)]
    [InlineData(typeof(SendChatActionRequest), false)]
    [InlineData(typeof(EditMessageTextRequest), false)]
    [InlineData(typeof(DeleteMessageRequest), false)]
    public void Only_new_messages_are_limited(Type requestType, bool limited)
    {
        var request = (Telegram.Bot.Requests.Abstractions.IRequest)Activator.CreateInstance(requestType)!;
        requestType.GetProperty("ChatId")!.SetValue(request, new Telegram.Bot.Types.ChatId(42));
        Assert.Equal(limited, FloodControlledBotClient.IsLimited(request, out _));
    }

    [Fact]
    public void Answers_without_a_chat_are_not_limited() =>
        Assert.False(FloodControlledBotClient.IsLimited(new AnswerCallbackQueryRequest { CallbackQueryId = "1" }, out _));

    [Fact]
    public async Task Messages_to_one_chat_wait_after_the_burst()
    {
        var fake = new FakeTelegramBotClient();
        using var client = new FloodControlledBotClient(fake,
            new TelegramBotFloodControlOptions { PrivateChatPerSecond = 10, PrivateChatBurst = 2, GlobalPerSecond = 1000 },
            NullLogger.Instance);

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 6; i++)
            await client.SendMessage(42, $"#{i}");
        watch.Stop();

        // 2 at once, then 4 more at 10 per second.
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
        Assert.Equal(6, fake.MessagesIn(42).Count);
    }

    [Fact]
    public async Task Different_chats_do_not_wait_for_each_other()
    {
        var fake = new FakeTelegramBotClient();
        using var client = new FloodControlledBotClient(fake,
            new TelegramBotFloodControlOptions { PrivateChatPerSecond = 1, PrivateChatBurst = 1, GlobalPerSecond = 1000 },
            NullLogger.Instance);

        var watch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(1, 20).Select(chat => client.SendMessage(chat, "hi")));
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), watch.Elapsed.ToString());
    }

    [Fact]
    public async Task Groups_get_their_per_minute_limit()
    {
        var fake = new FakeTelegramBotClient();
        using var client = new FloodControlledBotClient(fake,
            new TelegramBotFloodControlOptions { GroupPerMinute = 3, GlobalPerSecond = 1000 },
            NullLogger.Instance);

        for (var i = 0; i < 3; i++)
            await client.SendMessage(-100, "hi");

        var fourth = client.SendMessage(-100, "hi");
        var finished = await Task.WhenAny(fourth, Task.Delay(500));
        Assert.NotSame(fourth, finished);
    }

    [Fact]
    public void Configuration_is_validated()
    {
        var builder = (TelegramBotBuilder)typeof(TelegramBotBuilder)
            .GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0]
            .Invoke([new Microsoft.Extensions.DependencyInjection.ServiceCollection()]);
        var validator = new TelegramBotConfigurationValidator(builder);

        var result = validator.Validate(null, new TelegramBotConfiguration
        {
            Token = "1:x",
            FloodControl = new TelegramBotFloodControlOptions { GlobalPerSecond = 0, RetryCount = -1 }
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("GlobalPerSecond"));
        Assert.Contains(result.Failures!, failure => failure.Contains("RetryCount"));
    }
}

public sealed class CommandTextTests
{
    [Theory]
    [InlineData("/start", "start", null, null)]
    [InlineData("/start ref_1", "start", "ref_1", null)]
    [InlineData("/start@My_Bot  a b ", "start", "a b", "My_Bot")]
    [InlineData("/start\nline two", "start", "line two", null)]
    public void Parses_commands(string text, string command, string? args, string? mention)
    {
        Assert.True(CommandText.TryParse(text, out var c, out var a, out var m));
        Assert.Equal(command, c);
        Assert.Equal(args, a);
        Assert.Equal(mention, m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("hello")]
    [InlineData("/@bot")]
    public void Rejects_non_commands(string? text) => Assert.False(CommandText.TryParse(text, out _, out _, out _));
}
