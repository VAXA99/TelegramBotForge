using System.Collections.Concurrent;
using System.Reflection;

namespace TelegramBotForge.Views;

/// <summary>
/// A view that needs no data, described with a <see cref="MessageBuilder"/>.
/// </summary>
/// <example>
/// <code>
/// public sealed class MainMenuView : View
/// {
///     protected override void Build(MessageBuilder message) => message
///         .Line($"&lt;b&gt;Welcome!&lt;/b&gt;")
///         .Row(Button("📝 Register", "register"), Button("🛍 Catalog", "catalog:1"));
/// }
/// </code>
/// </example>
public abstract class View : ViewBase, IView
{
    /// <inheritdoc/>
    public BotMessage Render()
    {
        var message = CreateBuilder();
        Build(message);
        return message.Build();
    }

    /// <summary>
    /// Describes the message.
    /// </summary>
    protected abstract void Build(MessageBuilder message);
}

/// <summary>
/// A view that turns a model into a message, described with a <see cref="MessageBuilder"/>.
/// </summary>
/// <typeparam name="TModel">The data the view shows.</typeparam>
/// <example>
/// <code>
/// public sealed class ProductListView : View&lt;ProductPage&gt;
/// {
///     protected override void Build(MessageBuilder message, ProductPage model) => message
///         .Line($"&lt;b&gt;Our products&lt;/b&gt;")
///         .Page(model.Products, model.Page, pageSize: 5,
///               item: product => Button(product.Name, $"product:{product.Id}"),
///               route: page => $"products:{page}")
///         .Back("menu");
/// }
/// </code>
/// </example>
public abstract class View<TModel> : ViewBase, IView<TModel>
{
    /// <inheritdoc/>
    public BotMessage Render(TModel model)
    {
        var message = CreateBuilder();
        Build(message, model);
        return message.Build();
    }

    /// <summary>
    /// Describes the message for the model.
    /// </summary>
    protected abstract void Build(MessageBuilder message, TModel model);
}

/// <summary>
/// What <see cref="View"/> and <see cref="View{TModel}"/> share: the button helpers and the pagination defaults of the view.
/// </summary>
public abstract class ViewBase : IBotView
{
    private static readonly ConcurrentDictionary<Type, bool> RichViews = new();

    private protected ViewBase()
    {
    }

    /// <summary>
    /// Pagination defaults of this view, between the bot's defaults and the overrides of a single call.
    /// </summary>
    protected virtual void ConfigurePagination(PaginationOptions options)
    {
    }

    /// <summary>
    /// A button that sends <paramref name="data"/> to the bot. Without data, pressing it does nothing.
    /// </summary>
    protected static BotButton Button(string text, string? data = null) => Buttons.Callback(text, data);

    /// <summary>
    /// A button that opens a link.
    /// </summary>
    protected static BotButton UrlButton(string text, string url) => Buttons.Url(text, url);

    /// <summary>
    /// A button that copies a text to the user's clipboard.
    /// </summary>
    protected static BotButton CopyButton(string text, string textToCopy) => Buttons.Copy(text, textToCopy);

    /// <summary>
    /// HTML inserted without escaping, for example <c>.Line($"{Raw(html)}")</c>. Never use it for text from users.
    /// </summary>
    protected static RawHtml Raw(string html) => new(html ?? string.Empty);

    private protected MessageBuilder CreateBuilder()
    {
        var type = GetType();
        var rich = RichViews.GetOrAdd(type, t => t.GetCustomAttribute<ViewAttribute>()?.Rich ?? false);
        return new MessageBuilder(type.Name, rich, ConfigurePagination);
    }
}

/// <summary>
/// Defaults of all views of the bot. Set them with <c>ConfigureViews</c> at startup.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddTelegramBot(builder.Configuration)
///     .ConfigureViews(views =>
///     {
///         views.BackText = "⬅ Back";
///         views.Pagination(p => p.Previous("◀").Next("▶").AtEdges(PaginationEdge.Placeholder));
///     });
/// </code>
/// </example>
public sealed class ViewDefaults
{
    private readonly List<Action<PaginationOptions>> _pagination = [];

    /// <summary>
    /// The defaults of this application. They apply to every bot in the process.
    /// </summary>
    internal static ViewDefaults Current { get; } = new();

    /// <summary>Gets or sets the label of back buttons. "« Back" by default.</summary>
    public string BackText { get; set; } = "« Back";

    /// <summary>Gets or sets the label of next buttons. "Next »" by default.</summary>
    public string NextText { get; set; } = "Next »";

    /// <summary>Gets or sets the label of confirm buttons. "✅ Confirm" by default.</summary>
    public string ConfirmText { get; set; } = "✅ Confirm";

    /// <summary>Gets or sets the label of cancel buttons. "✖ Cancel" by default.</summary>
    public string CancelText { get; set; } = "✖ Cancel";

    /// <summary>
    /// Changes the pagination of every view. Can be called more than once; the changes are applied in order.
    /// </summary>
    public ViewDefaults Pagination(Action<PaginationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _pagination.Add(configure);
        return this;
    }

    internal PaginationOptions CreatePaginationOptions()
    {
        var options = new PaginationOptions();
        foreach (var configure in _pagination)
            configure(options);
        return options;
    }
}
