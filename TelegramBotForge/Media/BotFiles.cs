using Telegram.Bot;

namespace TelegramBotForge.Media;

/// <summary>
/// Downloads files users sent. Get it through the controller's constructor.
/// </summary>
/// <remarks>
/// The official Bot API server only lets bots download files up to 20 MB.
/// </remarks>
public interface IBotFiles
{
    /// <summary>
    /// Downloads a file into a stream.
    /// </summary>
    /// <param name="fileId">The file id, for example <see cref="BotMedia.FileId"/>.</param>
    /// <param name="destination">Where the file is written.</param>
    /// <param name="ct">Cancels the download.</param>
    Task DownloadAsync(string fileId, Stream destination, CancellationToken ct = default);

    /// <summary>
    /// Downloads a file to disk. A partly written file is deleted when the download fails.
    /// </summary>
    /// <param name="fileId">The file id, for example <see cref="BotMedia.FileId"/>.</param>
    /// <param name="path">The file to create or overwrite.</param>
    /// <param name="ct">Cancels the download.</param>
    Task DownloadToFileAsync(string fileId, string path, CancellationToken ct = default);
}

internal sealed class TelegramBotFiles : IBotFiles
{
    private readonly ITelegramBotClient _bot;

    public TelegramBotFiles(ITelegramBotClient bot) => _bot = bot;

    public async Task DownloadAsync(string fileId, Stream destination, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentNullException.ThrowIfNull(destination);

        var file = await _bot.GetFile(fileId, cancellationToken: ct);
        if (string.IsNullOrEmpty(file.FilePath))
        {
            throw new InvalidOperationException(
                "Telegram returned no download path for this file. Files over 20 MB cannot be downloaded through the Bot API.");
        }

        await _bot.DownloadFile(file.FilePath, destination, cancellationToken: ct);
    }

    public async Task DownloadToFileAsync(string fileId, string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            await using var stream = System.IO.File.Create(path);
            await DownloadAsync(fileId, stream, ct);
        }
        catch
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
            throw;
        }
    }
}
