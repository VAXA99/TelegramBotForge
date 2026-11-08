using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace TelegramBotForge.Hosting;

/// <summary>
/// A Telegram client that passes every call to another one. Derive from it and override <see cref="SendRequest{TResponse}"/>
/// to add behavior around all Bot API calls, like the library's flood control and telemetry do.
/// </summary>
public abstract class DelegatingBotClient : ITelegramBotClient
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DelegatingBotClient"/> class.
    /// </summary>
    /// <param name="inner">The client that makes the calls.</param>
    protected DelegatingBotClient(ITelegramBotClient inner) => Inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>
    /// Gets the client that makes the calls.
    /// </summary>
    protected ITelegramBotClient Inner { get; }

    /// <inheritdoc/>
    public bool LocalBotServer => Inner.LocalBotServer;

    /// <inheritdoc/>
    public long BotId => Inner.BotId;

    /// <inheritdoc/>
    public TimeSpan Timeout
    {
        get => Inner.Timeout;
        set => Inner.Timeout = value;
    }

    /// <inheritdoc/>
    public IExceptionParser ExceptionsParser
    {
        get => Inner.ExceptionsParser;
        set => Inner.ExceptionsParser = value;
    }

    /// <inheritdoc/>
    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest
    {
        add => Inner.OnMakingApiRequest += value;
        remove => Inner.OnMakingApiRequest -= value;
    }

    /// <inheritdoc/>
    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived
    {
        add => Inner.OnApiResponseReceived += value;
        remove => Inner.OnApiResponseReceived -= value;
    }

    /// <inheritdoc/>
    public virtual Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        Inner.SendRequest(request, cancellationToken);

    /// <inheritdoc/>
    public virtual Task<bool> TestApi(CancellationToken cancellationToken = default) => Inner.TestApi(cancellationToken);

    /// <inheritdoc/>
    public virtual Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) =>
        Inner.DownloadFile(filePath, destination, cancellationToken);

    /// <inheritdoc/>
    public virtual Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) =>
        Inner.DownloadFile(file, destination, cancellationToken);
}
