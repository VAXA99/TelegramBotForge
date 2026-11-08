using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using TelegramBotForge.Hosting;

namespace TelegramBotForge.Tests;

public sealed class QueueTests
{
    [Fact]
    public async Task Updates_of_one_chat_are_processed_in_order_and_finished_on_stop()
    {
        await using var bot = TestBot.Create();
        var queues = bot.Services.GetRequiredService<UpdateQueues>();
        await queues.StartAsync(CancellationToken.None);

        var user = new User { Id = 555, FirstName = "Q" };
        var chat = new Chat { Id = 555, Type = Telegram.Bot.Types.Enums.ChatType.Private };
        for (var i = 1; i <= 50; i++)
        {
            await queues.EnqueueAsync(new Update
            {
                Id = i,
                Message = new Message { Id = i, Date = DateTime.UtcNow, Chat = chat, From = user, Text = $"/echo {i}" }
            }, CancellationToken.None);
        }

        await queues.StopAsync(CancellationToken.None);

        var texts = bot.Telegram.RequestsOf<SendMessageRequest>().Select(request => request.Text).ToList();
        Assert.Equal(Enumerable.Range(1, 50).Select(i => i.ToString()), texts);
    }
}

public sealed class RateLimitTests
{
    [Fact]
    public async Task Too_many_updates_get_one_warning_then_silence()
    {
        await using var bot = TestBot.Create(configuration: new Dictionary<string, string?>
        {
            ["RateLimit:Enabled"] = "true",
            ["RateLimit:MaxUpdates"] = "2",
            ["RateLimit:PerSeconds"] = "60"
        });
        var user = bot.PrivateChat();

        Assert.Equal("Hello!", (await user.SendText("/start")).Text);
        Assert.Equal("Hello!", (await user.SendText("/start")).Text);
        var warned = await user.SendText("/start");
        Assert.NotEqual("Hello!", warned.Text);
        Assert.True((await user.SendText("/start")).IsEmpty);
    }
}
