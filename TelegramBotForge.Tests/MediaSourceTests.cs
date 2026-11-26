using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramBotForge.Controllers;
using TelegramBotForge.Hosting;
using TelegramBotForge.Media;
using TelegramBotForge.Results;
using TelegramBotForge.Testing;

namespace TelegramBotForge.Tests;

public sealed class MediaSourceProbe
{
    public List<BotMedia> Items { get; } = [];
    public IReadOnlyList<Message>? Messages { get; set; }
}

public sealed class MediaSourceController<TMarker>(MediaSourceProbe probe) : BotController
{
    [Media]
    public IBotResult Single(BotMedia media)
    {
        Assert.Same(Context.Media, media);
        probe.Items.Add(media);
        return new MultiResult();
    }

    [Album]
    public async Task<IBotResult> Album(IReadOnlyList<BotMedia> items, IReadOnlyList<Message> messages, CancellationToken ct)
    {
        Assert.Same(Context.Album, items);
        Assert.Same(Context.AlbumMessages, messages);
        probe.Items.AddRange(items);
        probe.Messages = messages;
        var results = new List<IBotResult>();
        foreach (var media in items.Where(media => media.Type == MediaType.Photo).Reverse())
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (media.SourceMessageId is { } id)
                results.Add(new DeleteMessage(id));
        }
        return new MultiResult(results.ToArray());
    }
}

public sealed class MediaSourceTests
{
    private static Message Incoming(int id) => new()
    {
        Id = id, Chat = new Chat { Id = 777, Type = ChatType.Private },
        From = new User { Id = 777, FirstName = "Media" }, Date = DateTime.UtcNow,
        Caption = "caption", MediaGroupId = "album"
    };

    private static PhotoSize Photo(string file = "photo", int width = 10, int height = 20) => new()
    {
        FileId = file, FileUniqueId = "unique-" + file, Width = width, Height = height, FileSize = 100
    };

    private static Message WithMedia(MediaType type, int id)
    {
        var message = Incoming(id);
        switch (type)
        {
            case MediaType.Photo: message.Photo = [Photo("small", 1, 1), Photo("largest", 30, 40), Photo("last", 2, 2)]; break;
            case MediaType.Video: message.Video = new Video { FileId = "video", FileUniqueId = "unique", Width = 30, Height = 40, Duration = 5 }; break;
            case MediaType.Animation:
                message.Animation = new Animation { FileId = "animation", FileUniqueId = "unique", Width = 30, Height = 40, Duration = 5 };
                message.Document = new Document { FileId = "document", FileUniqueId = "unique" };
                break;
            case MediaType.Document: message.Document = new Document { FileId = "document", FileUniqueId = "unique" }; break;
            case MediaType.Audio: message.Audio = new Audio { FileId = "audio", FileUniqueId = "unique", Duration = 5 }; break;
            case MediaType.Voice: message.Voice = new Voice { FileId = "voice", FileUniqueId = "unique", Duration = 5 }; break;
            case MediaType.VideoNote: message.VideoNote = new VideoNote { FileId = "video-note", FileUniqueId = "unique", Duration = 5, Length = 30 }; break;
            case MediaType.Sticker: message.Sticker = new Sticker { FileId = "sticker", FileUniqueId = "unique", Width = 30, Height = 40 }; break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
        return message;
    }

    [Theory]
    [InlineData(MediaType.Photo)]
    [InlineData(MediaType.Video)]
    [InlineData(MediaType.Animation)]
    [InlineData(MediaType.Document)]
    [InlineData(MediaType.Audio)]
    [InlineData(MediaType.Voice)]
    [InlineData(MediaType.VideoNote)]
    [InlineData(MediaType.Sticker)]
    public void Every_supported_type_retains_its_real_message_id_and_existing_metadata(MediaType type)
    {
        var message = WithMedia(type, 456);
        var media = Assert.IsType<BotMedia>(BotMedia.From(message));
        Assert.Equal(type, media.Type);
        Assert.Equal(message.Id, media.SourceMessageId);
        Assert.Equal("caption", media.Caption);
        Assert.Equal("album", media.MediaGroupId);
        if (type == MediaType.Photo)
        {
            Assert.Equal("largest", media.FileId);
            Assert.Equal(30, media.Width);
            Assert.Equal(40, media.Height);
        }
        if (type == MediaType.Animation)
            Assert.Equal("animation", media.FileId);
    }

    [Fact]
    public void Null_empty_and_manual_creation_keep_old_behavior_and_constructor_deconstruction()
    {
        Assert.Null(BotMedia.From(null));
        Assert.Null(BotMedia.From(Incoming(123)));
        var original = new BotMedia(MediaType.Photo, "file", "unique", 10, 20, 30, null, null, null, "caption", "album");
        Assert.Null(original.SourceMessageId);
        var (type, file, unique, size, width, height, duration, mime, name, caption, group) = original;
        Assert.Equal(MediaType.Photo, type);
        Assert.Equal("file", file);
        Assert.Equal("unique", unique);
        Assert.Equal(10, size);
        Assert.Equal(20, width);
        Assert.Equal(30, height);
        Assert.Null(duration);
        Assert.Null(mime);
        Assert.Null(name);
        Assert.Equal("caption", caption);
        Assert.Equal("album", group);
        var received = original with { SourceMessageId = 456 };
        Assert.Equal(456, (received with { Caption = "changed" }).SourceMessageId);
        Assert.Equal(received, received with { });
        Assert.Equal(received.GetHashCode(), (received with { }).GetHashCode());
        Assert.NotEqual(original, received);
        Assert.NotEqual(received, received with { SourceMessageId = 789 });
    }

    private static BotTestHost Create(MediaSourceProbe probe) => BotTestHost.Create<BotMedia>(
        services => services.AddSingleton(probe),
        builder => builder.BuiltInControllers.Add(typeof(MediaSourceController<object>)));

    [Fact]
    public async Task Single_media_parameter_matches_context_and_never_deletes_automatically()
    {
        var probe = new MediaSourceProbe();
        await using var bot = Create(probe);
        var source = WithMedia(MediaType.Video, 345);
        var result = await bot.SendUpdate(new Update { Message = source });
        Assert.Equal(source.Id, Assert.Single(probe.Items).SourceMessageId);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public async Task Mixed_album_can_filter_reorder_and_delete_only_selected_sources_even_for_identical_files()
    {
        var probe = new MediaSourceProbe();
        await using var bot = Create(probe);
        var sources = new[] { WithMedia(MediaType.Photo, 10), WithMedia(MediaType.Video, 20),
            WithMedia(MediaType.Photo, 30), WithMedia(MediaType.Document, 40) };
        await bot.Services.GetRequiredService<UpdateDispatcher>().DispatchAsync(
            new Update { Message = sources[0] }, false, CancellationToken.None, sources);
        Assert.Equal(new int?[] { 10, 20, 30, 40 }, probe.Items.Select(media => media.SourceMessageId));
        Assert.Equal(new[] { MediaType.Photo, MediaType.Video, MediaType.Photo, MediaType.Document }, probe.Items.Select(media => media.Type));
        Assert.Equal(probe.Items[0].FileId, probe.Items[2].FileId);
        Assert.Equal(probe.Items[0].FileUniqueId, probe.Items[2].FileUniqueId);
        Assert.NotEqual(probe.Items[0], probe.Items[2]);
        Assert.Equal(sources, probe.Messages);
        var deleted = bot.Telegram.RequestsOf<DeleteMessageRequest>();
        Assert.Equal(new[] { 30, 10 }, deleted.Select(request => request.MessageId));
        Assert.All(deleted, request => Assert.Equal(777, request.ChatId.Identifier));
    }

    [Fact]
    public async Task Queued_album_preserves_existing_message_sorting_and_source_identity()
    {
        var probe = new MediaSourceProbe();
        await using var bot = Create(probe);
        var queues = bot.Services.GetRequiredService<UpdateQueues>();
        await queues.StartAsync(CancellationToken.None);
        foreach (var id in new[] { 30, 10, 20 })
            await queues.EnqueueAsync(new Update { Id = id, Message = WithMedia(MediaType.Photo, id) }, CancellationToken.None);
        await queues.StopAsync(CancellationToken.None);
        Assert.Equal(new int?[] { 10, 20, 30 }, probe.Items.Select(media => media.SourceMessageId));
        Assert.Equal(new[] { 10, 20, 30 }, probe.Messages!.Select(message => message.Id));
        Assert.Equal(new[] { 30, 20, 10 }, bot.Telegram.RequestsOf<DeleteMessageRequest>().Select(request => request.MessageId));
    }
}
