using System.Runtime.CompilerServices;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Media;
using TelegramBotForge.Results;

namespace TelegramBotForge.Views;

/// <summary>An immutable content description. Each fluent call returns a new message.</summary>
public sealed class Message
{
    /// <summary>Starts a screen in an annotated class that does not inherit View.</summary>
    public static Message Create<TView>([CallerMemberName] string screen = "") =>
        new(typeof(TView).FullName + "." + screen, ViewMetadata.For(typeof(TView), screen), _ => { });

    internal string Origin { get; }
    internal bool Rich { get; }
    internal Content[] Content { get; }
    internal Action<PaginationOptions> ConfigurePagination { get; }
    internal Func<MessageDelivery, CancellationToken, Task>? Delivered { get; }
    internal Func<CancellationToken, Task>? Finished { get; }
    internal TimeSpan? Lifetime { get; }
    private object Lineage { get; }

    internal Message(string origin, bool rich, Action<PaginationOptions> configurePagination,
        Content[]? content = null, Func<MessageDelivery, CancellationToken, Task>? delivered = null, object? lineage = null,
        Func<CancellationToken, Task>? finished = null, TimeSpan? lifetime = null)
    {
        Origin = origin;
        Rich = rich;
        ConfigurePagination = configurePagination;
        Content = content ?? [];
        Delivered = delivered;
        Finished = finished;
        Lifetime = lifetime;
        Lineage = lineage ?? new object();
    }

    private Message Append(Content content) => new(Origin, Rich, ConfigurePagination, [.. Content, content], Delivered, Lineage, Finished, Lifetime);
    public Message Heading(Text content) => Append(new InlineContent(content, true));
    public Message Paragraph(Text content) => Append(new InlineContent(content, false));
    public Message Link(Text label, string url) => Paragraph(Text.Link(label, url));
    public Message Space() => Append(new SpaceContent());
    public Message RawHtml(string trustedHtml) => Append(new RawContent(trustedHtml ?? throw new ArgumentNullException(nameof(trustedHtml))));
    public Message Row(params BotButton[] buttons) => Append(new ButtonsContent(buttons.ToArray()));
    public Message Buttons<T>(IEnumerable<T> items, Func<T, BotButton> button, int perRow = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perRow, 1);
        var message = this;
        foreach (var row in items.Select(button).Chunk(perRow))
            message = message.Row(row);
        return message;
    }
    public Message Pagination(int page, int totalPages, Func<int, string> route, Action<PaginationOptions>? configure = null)
    {
        var options = ViewDefaults.Current.CreatePaginationOptions();
        ConfigurePagination(options);
        configure?.Invoke(options);
        var total = Math.Max(1, totalPages);
        var message = this;
        foreach (var row in options.BuildRows(new PageInfo(Math.Clamp(page, 1, total), total, route, options, Rich)))
            message = message.Row(row.ToArray());
        return message;
    }
    public Message Page<T>(IReadOnlyList<T> items, int page, int pageSize,
        Func<Message, IReadOnlyList<T>, PageInfo, Message> content, Func<int, string> route,
        Action<PaginationOptions>? configure = null)
    {
        var window = PageWindow.Of(items.Count, page, pageSize);
        var options = ViewDefaults.Current.CreatePaginationOptions();
        ConfigurePagination(options);
        configure?.Invoke(options);
        var info = new PageInfo(window.Page, window.TotalPages, route, options, Rich, items.Count, pageSize);
        var message = Component(body => content(body, window.Slice(items), info));
        foreach (var row in options.BuildRows(info))
            message = message.Row(row.ToArray());
        return message;
    }
    public Message ReplyButtons(IEnumerable<string> labels, int perRow = 1) => Append(new ReplyContent(labels.ToArray(), perRow));
    public Message RemoveKeyboard() => Append(new RemoveKeyboardContent());
    public Message RequestContact(string label) => Append(new RequestContent(label, false));
    public Message RequestLocation(string label) => Append(new RequestContent(label, true));
    public Message RequestUsers(string label, KeyboardButtonRequestUsers request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return Append(new RequestUsersContent(label, MessageBuilder.SnapshotRequestUsers(request)));
    }
    public Message Table(Func<MessageTable, MessageTable> table) => Append(new TableContent(table(new MessageTable()).Rows));
    public Message Details(Text summary, Func<Message, Message> body) => Append(new DetailsContent(summary, ComponentBody(body)));
    public Message Component(Func<Message, Message> component) => Append(new ComponentContent(ComponentBody(component)));
    private Content[] ComponentBody(Func<Message, Message> component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var seed = new Message(Origin, Rich, ConfigurePagination);
        var result = component(seed) ?? throw new InvalidOperationException($"View {Origin}: component returned null.");
        if (result.Lineage != seed.Lineage)
            throw new InvalidOperationException($"View {Origin}: components must append to their supplied message.");
        if (result.Delivered is not null || result.Finished is not null || result.Lifetime is not null)
            throw new InvalidOperationException($"View {Origin}: attach delivery callbacks to the root message, not a component.");
        return result.Content;
    }
    public Message Photo(MediaFile photo) => photo.Type == MediaType.Photo ? Slideshow([photo])
        : throw new ArgumentException($"View {Origin}: Photo requires photo media.", nameof(photo));
    public Message Video(MediaFile video) => video.Type == MediaType.Video ? Slideshow([video])
        : throw new ArgumentException($"View {Origin}: Video requires video media.", nameof(video));
    public Message Slideshow(IEnumerable<MediaFile> media) => Append(new MediaContent(media.ToArray()));
    public Message OnDelivered(Func<MessageDelivery, CancellationToken, Task> callback) =>
        new(Origin, Rich, ConfigurePagination, Content, callback ?? throw new ArgumentNullException(nameof(callback)), Lineage, Finished, Lifetime);

    public Message Finally(Func<CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new(Origin, Rich, ConfigurePagination, Content, Delivered, Lineage,
            Finished is { } previous ? token => ResultLifecycleExtensions.FinishAsync(() => previous(token), callback) : callback, Lifetime);
    }

    public Message Finally(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return Finally(_ => { callback(); return Task.CompletedTask; });
    }

    public Message Finally(Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return Finally(_ => callback());
    }

    public Message DeleteAfter(TimeSpan delay)
    {
        ResultLifecycleExtensions.ValidateDelay(delay);
        return new(Origin, Rich, ConfigurePagination, Content, Delivered, Lineage, Finished, delay);
    }
}

/// <summary>Immutable typed table rows and cells.</summary>
public sealed class MessageTable
{
    internal Text[][] Rows { get; }
    public MessageTable() : this([]) { }
    private MessageTable(Text[][] rows) => Rows = rows;
    public MessageTable Row(params Text[] cells) => new([.. Rows, cells.ToArray()]);
    public MessageTable Field(Text label, Text value) => Row(label, value);
}

internal abstract record Content;
internal sealed record InlineContent(Text Text, bool Heading) : Content;
internal sealed record SpaceContent : Content;
internal sealed record RawContent(string Html) : Content;
internal sealed record ButtonsContent(BotButton[] Buttons) : Content;
internal sealed record TableContent(Text[][] Rows) : Content;
internal sealed record DetailsContent(Text Summary, Content[] Body) : Content;
internal sealed record ComponentContent(Content[] Body) : Content;
internal sealed record MediaContent(MediaFile[] Media) : Content;
internal sealed record ReplyContent(string[] Labels, int PerRow) : Content;
internal sealed record RemoveKeyboardContent : Content;
internal sealed record RequestContent(string Label, bool Location) : Content;
internal sealed record RequestUsersContent(string Label, KeyboardButtonRequestUsers Request) : Content;
