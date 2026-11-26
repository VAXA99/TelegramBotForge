using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Requests;
using TelegramBotForge.Controllers;
using TelegramBotForge.Results;
using TelegramBotForge.Views;
using Message = TelegramBotForge.Views.Message;

namespace TelegramBotForge.Tests;

public sealed class ScreenAwaiter
{
    public TaskCompletionSource<string> Value { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<string> Read(CancellationToken ct)
    {
        Started.TrySetResult();
        return await Value.Task.WaitAsync(ct);
    }
}

[View(Rich = true)]
public sealed class AsyncScreenView(ScreenAwaiter source) : View
{
    public async Task<Message> OpenAsync(CancellationToken ct)
    {
        var name = await source.Read(ct);
        return Message().Heading(name);
    }
    [Standard]
    public async ValueTask<Message> StandardAsync(CancellationToken ct)
    {
        var name = await source.Read(ct);
        return Message().Paragraph(name);
    }
}

public sealed class AsyncScreenController(AsyncScreenView view) : BotController
{
    [Command("async_screen_task")]
    public async Task<IBotResult> Open(CancellationToken ct) => Reply(await view.OpenAsync(ct));
    [Command("async_screen_value")]
    public async Task<IBotResult> Standard(CancellationToken ct) => Reply(await view.StandardAsync(ct));
}

public sealed class AsyncScreenTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Awaited_screens_preserve_their_name_and_annotations(bool valueTask)
    {
        var source = new ScreenAwaiter();
        var view = new AsyncScreenView(source);
        var building = valueTask ? view.StandardAsync(CancellationToken.None).AsTask() : view.OpenAsync(CancellationToken.None);
        Assert.False(building.IsCompleted);
        source.Value.SetResult("Async name");
        var message = await building;
        Assert.EndsWith(valueTask ? ".StandardAsync" : ".OpenAsync", message.Origin);
        Assert.Equal(!valueTask, MessageEvaluation.Evaluate(message).Message.Rich);
        Assert.Contains("Async name", MessageEvaluation.Evaluate(message).Message.Text);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Screen_build_errors_and_cancellation_use_the_pipeline(bool valueTask, bool cancel)
    {
        var source = new ScreenAwaiter();
        await using var bot = TestBot.Create(services: services => services.AddSingleton(source));
        using var cancellation = new CancellationTokenSource();
        var processing = bot.PrivateChat().SendText(valueTask ? "/async_screen_value" : "/async_screen_task", cancellation.Token);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
            cancellation.Cancel();
        else
            source.Value.SetException(new InvalidOperationException("screen failed"));
        var result = await processing;
        Assert.Empty(bot.Telegram.RequestsOf<SendRichMessageRequest>());
        if (cancel)
            Assert.Empty(result.Requests);
        else
            Assert.Single(bot.Telegram.RequestsOf<SendMessageRequest>());
    }

    [Fact]
    public async Task Inherited_modes_and_plain_annotated_async_factories_work()
    {
        var view = new DerivedAsyncView();
        Assert.True((await view.RichAsync()).Rich);
        Assert.False((await view.StandardAsync()).Rich);
        Assert.True((await new PlainAsyncView().OpenAsync()).Rich);
    }

    [Fact]
    public void Metadata_validation_never_invokes_screens_and_rejects_conflicts_and_duplicate_names()
    {
        ViewMetadata.Validate(typeof(NotExecuted));
        ViewMetadata.Validate(typeof(NotExecuted));
        Assert.Equal(0, NotExecuted.Calls);
        Assert.Contains("conflicting", Assert.Throws<InvalidOperationException>(() => ViewMetadata.Validate(typeof(Conflict))).Message);
        Assert.Contains("conflicting", Assert.Throws<InvalidOperationException>(() => ViewMetadata.Validate(typeof(ConflictingOverride))).Message);
        Assert.Contains("ambiguous", Assert.Throws<InvalidOperationException>(() => ViewMetadata.Validate(typeof(Overloaded))).Message);
        Assert.Contains("ambiguous", Assert.Throws<InvalidOperationException>(() => ViewMetadata.Validate(typeof(Hidden))).Message);
    }

    [View(Rich = true)]
    public abstract class AsyncBase : View
    {
        [Rich] public virtual async Task<Message> RichAsync() { await Task.Yield(); return Message().Paragraph("rich"); }
        [Standard] public virtual async ValueTask<Message> StandardAsync() { await Task.Yield(); return Message().Paragraph("standard"); }
    }
    public sealed class DerivedAsyncView : AsyncBase
    {
        public override async Task<Message> RichAsync() { await Task.Yield(); return Message().Paragraph("derived"); }
        public override async ValueTask<Message> StandardAsync() { await Task.Yield(); return Message().Paragraph("derived"); }
    }
    [View(Rich = true)]
    public sealed class PlainAsyncView
    {
        public async ValueTask<Message> OpenAsync() { await Task.Yield(); return Message.Create<PlainAsyncView>().Paragraph("plain"); }
    }
    private abstract class NotExecuted : View
    {
        public static int Calls;
        public Task<Message> ScreenAsync() { Calls++; throw new InvalidOperationException(); }
    }
    private abstract class Conflict : View
    {
        [Rich, Standard] public Task<Message> ScreenAsync() => throw new InvalidOperationException();
    }
    private abstract class ConflictingBase : View
    {
        [Rich, Standard] public virtual ValueTask<Message> ScreenAsync() => throw new InvalidOperationException();
    }
    private abstract class ConflictingOverride : ConflictingBase
    {
        [Standard] public override ValueTask<Message> ScreenAsync() => throw new InvalidOperationException();
    }
    private abstract class Overloaded : View
    {
        public Task<Message> ScreenAsync() => throw new InvalidOperationException();
        public ValueTask<Message> ScreenAsync(int page) => throw new InvalidOperationException();
    }
    private abstract class HiddenBase : View
    {
        public Task<Message> ScreenAsync() => throw new InvalidOperationException();
    }
    private abstract class Hidden : HiddenBase
    {
        public new ValueTask<Message> ScreenAsync() => throw new InvalidOperationException();
    }
}
