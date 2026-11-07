using Telegram.Bot.Types;

namespace TelegramBotForge.Media;

/// <summary>
/// The kinds of media a message can carry.
/// </summary>
public enum MediaType
{
    Photo,
    Video,
    Animation,
    Document,
    Audio,
    Voice,
    VideoNote,
    Sticker
}

/// <summary>
/// The media of a received message, in one shape for every media type. An action gets it as a <c>BotMedia</c> parameter.
/// </summary>
/// <remarks>
/// An album arrives as one update per item; items of the same album share <see cref="MediaGroupId"/>.
/// </remarks>
/// <param name="Type">What kind of media it is.</param>
/// <param name="FileId">The id to download the file or to send it again.</param>
/// <param name="FileUniqueId">An id that stays the same for the same file, for example to detect duplicates. It cannot be used to download.</param>
/// <param name="FileSize">The size in bytes, if Telegram reports it.</param>
/// <param name="Width">The width in pixels (photo, video, animation, video note, sticker).</param>
/// <param name="Height">The height in pixels (photo, video, animation, video note, sticker).</param>
/// <param name="Duration">The duration in seconds (video, animation, audio, voice, video note).</param>
/// <param name="MimeType">The MIME type, if Telegram reports it.</param>
/// <param name="FileName">The original file name (document, video, animation, audio).</param>
/// <param name="Caption">The caption the user wrote, if any.</param>
/// <param name="MediaGroupId">The album the media belongs to, if any.</param>
public sealed record BotMedia(
    MediaType Type,
    string FileId,
    string FileUniqueId,
    long? FileSize,
    int? Width,
    int? Height,
    int? Duration,
    string? MimeType,
    string? FileName,
    string? Caption,
    string? MediaGroupId)
{
    /// <summary>
    /// Reads the media of a message, or returns <c>null</c> when it has none.
    /// For a photo, Telegram sends several sizes; the largest one is used.
    /// </summary>
    public static BotMedia? From(Message? message)
    {
        if (message is null)
            return null;

        var caption = message.Caption;
        var album = message.MediaGroupId;

        if (message.Photo is { Length: > 0 } sizes)
        {
            var photo = sizes.MaxBy(size => (long)size.Width * size.Height)!;
            return new BotMedia(MediaType.Photo, photo.FileId, photo.FileUniqueId, photo.FileSize,
                photo.Width, photo.Height, null, null, null, caption, album);
        }

        // An animation message also has a Document, so the animation is checked first.
        if (message.Animation is { } animation)
        {
            return new BotMedia(MediaType.Animation, animation.FileId, animation.FileUniqueId, animation.FileSize,
                animation.Width, animation.Height, animation.Duration, animation.MimeType, animation.FileName, caption, album);
        }

        if (message.Video is { } video)
        {
            return new BotMedia(MediaType.Video, video.FileId, video.FileUniqueId, video.FileSize,
                video.Width, video.Height, video.Duration, video.MimeType, video.FileName, caption, album);
        }

        if (message.Document is { } document)
        {
            return new BotMedia(MediaType.Document, document.FileId, document.FileUniqueId, document.FileSize,
                null, null, null, document.MimeType, document.FileName, caption, album);
        }

        if (message.Audio is { } audio)
        {
            return new BotMedia(MediaType.Audio, audio.FileId, audio.FileUniqueId, audio.FileSize,
                null, null, audio.Duration, audio.MimeType, audio.FileName, caption, album);
        }

        if (message.Voice is { } voice)
        {
            return new BotMedia(MediaType.Voice, voice.FileId, voice.FileUniqueId, voice.FileSize,
                null, null, voice.Duration, voice.MimeType, null, caption, album);
        }

        if (message.VideoNote is { } note)
        {
            return new BotMedia(MediaType.VideoNote, note.FileId, note.FileUniqueId, note.FileSize,
                note.Length, note.Length, note.Duration, null, null, caption, album);
        }

        if (message.Sticker is { } sticker)
        {
            return new BotMedia(MediaType.Sticker, sticker.FileId, sticker.FileUniqueId, sticker.FileSize,
                sticker.Width, sticker.Height, null, null, null, caption, album);
        }

        return null;
    }
}

/// <summary>
/// Where the file of outgoing media comes from.
/// </summary>
public enum MediaSource
{
    /// <summary>A Telegram file id: sent instantly, nothing is uploaded.</summary>
    FileId,

    /// <summary>A public URL that Telegram downloads itself.</summary>
    Url,

    /// <summary>A local file, uploaded when the message is sent. Relative paths are looked up next to the application, then in the working directory.</summary>
    Path
}

/// <summary>
/// Media the bot sends, for example <c>MediaFile.FromPath(MediaType.Photo, "images/menu.jpg")</c>.
/// </summary>
/// <param name="Type">What kind of media it is.</param>
/// <param name="Source">Where the file comes from.</param>
/// <param name="Value">The file id, URL or path.</param>
public sealed record MediaFile(MediaType Type, MediaSource Source, string Value)
{
    /// <summary>
    /// Media already on Telegram's servers, for example a file a user sent.
    /// </summary>
    public static MediaFile FromFileId(MediaType type, string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        return new MediaFile(type, MediaSource.FileId, fileId);
    }

    /// <summary>
    /// Media that Telegram downloads from a public URL.
    /// </summary>
    public static MediaFile FromUrl(MediaType type, string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            throw new ArgumentException($"\"{url}\" is not an absolute URL.", nameof(url));
        return new MediaFile(type, MediaSource.Url, url);
    }

    /// <summary>
    /// A local file, uploaded when the message is sent.
    /// </summary>
    public static MediaFile FromPath(MediaType type, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new MediaFile(type, MediaSource.Path, path);
    }

    /// <summary>
    /// The same media a user sent, sent again by its file id.
    /// </summary>
    public static MediaFile From(BotMedia received)
    {
        ArgumentNullException.ThrowIfNull(received);
        return FromFileId(received.Type, received.FileId);
    }
}
