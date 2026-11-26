namespace TelegramBotForge.Hosting;

internal sealed class BotContextScope : IDisposable
{
    private BotContext? _context;

    internal BotContext Required => _context ?? throw new InvalidOperationException(
        "BotContext is available only inside a TelegramBotForge update or background delivery operation. Resolve it from that operation's scoped services.");

    internal void Initialize(BotContext context)
    {
        if (_context is not null && !ReferenceEquals(_context, context))
            throw new InvalidOperationException("This scope already belongs to another bot operation.");
        _context = context;
    }

    public void Dispose() => _context = null;
}
