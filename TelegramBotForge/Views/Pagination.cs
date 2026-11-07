namespace TelegramBotForge.Views;

/// <summary>
/// The buttons of a pagination row.
/// </summary>
public enum PaginationStyle
{
    /// <summary><c>« | 2 / 5 | »</c>. The default.</summary>
    Compact,

    /// <summary><c>1 | « | 3 | [4] | 5 | » | 9</c></summary>
    Numbered,

    /// <summary><c>⏮ | « | 4 / 9 | » | ⏭</c></summary>
    FirstLast
}

/// <summary>
/// What the previous and next buttons do on the first and last page.
/// </summary>
public enum PaginationEdge
{
    /// <summary>They disappear. The default.</summary>
    Hide,

    /// <summary>They become a "·" that does nothing, so the row keeps its width. In rich messages, a disabled button.</summary>
    Placeholder,

    /// <summary>Previous on the first page goes to the last page, next on the last page to the first.</summary>
    Wrap
}

/// <summary>
/// How a pagination row looks. Set it for the whole bot with <c>ConfigureViews</c>, for one view by overriding
/// <c>ConfigurePagination</c>, or for one call; the more specific setting wins.
/// </summary>
/// <example>
/// <code>
/// message.Pagination(page, totalPages, p => $"products:{p}", pagination => pagination
///     .Previous("◀ Prev")
///     .Next(p => Button("▶", p.Route(p.NextPage)).Style(ButtonStyle.Primary))
///     .Layout(p => [p.PreviousButton, p.CounterButton, p.NextButton, Button("🔍", "products:search")]));
/// </code>
/// </example>
public sealed class PaginationOptions
{
    internal string PreviousText { get; private set; } = "«";
    internal string NextText { get; private set; } = "»";
    internal string FirstText { get; private set; } = "⏮";
    internal string LastText { get; private set; } = "⏭";
    internal string PlaceholderText { get; private set; } = "·";
    internal Func<PageInfo, string> CounterText { get; private set; } = p => $"{p.Page} / {p.TotalPages}";
    internal Func<PageInfo, string> CurrentPageText { get; private set; } = p => $"[{p.Page}]";
    internal Func<PageInfo, BotButton?>? PreviousOverride { get; private set; }
    internal Func<PageInfo, BotButton?>? NextOverride { get; private set; }
    internal Func<PageInfo, BotButton?>? FirstOverride { get; private set; }
    internal Func<PageInfo, BotButton?>? LastOverride { get; private set; }
    internal Func<PageInfo, BotButton?>? CounterOverride { get; private set; }
    internal Func<PageInfo, IEnumerable<IEnumerable<BotButton?>>>? RowsOverride { get; private set; }
    internal PaginationStyle Look { get; private set; } = PaginationStyle.Compact;
    internal PaginationEdge Edge { get; private set; } = PaginationEdge.Hide;
    internal bool HideSinglePage { get; private set; } = true;

    /// <summary>Sets the label of the previous button.</summary>
    public PaginationOptions Previous(string text) => Set(() => PreviousText = Required(text));

    /// <summary>Replaces the previous button. Use <c>p.Route(p.PreviousPage)</c> for its data.</summary>
    public PaginationOptions Previous(Func<PageInfo, BotButton?> button) => Set(() => PreviousOverride = button);

    /// <summary>Sets the label of the next button.</summary>
    public PaginationOptions Next(string text) => Set(() => NextText = Required(text));

    /// <summary>Replaces the next button. Use <c>p.Route(p.NextPage)</c> for its data.</summary>
    public PaginationOptions Next(Func<PageInfo, BotButton?> button) => Set(() => NextOverride = button);

    /// <summary>Sets the label of the first-page button (<see cref="PaginationStyle.FirstLast"/>).</summary>
    public PaginationOptions First(string text) => Set(() => FirstText = Required(text));

    /// <summary>Replaces the first-page button.</summary>
    public PaginationOptions First(Func<PageInfo, BotButton?> button) => Set(() => FirstOverride = button);

    /// <summary>Sets the label of the last-page button (<see cref="PaginationStyle.FirstLast"/>).</summary>
    public PaginationOptions Last(string text) => Set(() => LastText = Required(text));

    /// <summary>Replaces the last-page button.</summary>
    public PaginationOptions Last(Func<PageInfo, BotButton?> button) => Set(() => LastOverride = button);

    /// <summary>Sets the label of the counter, for example <c>p => $"Page {p.Page} of {p.TotalPages}"</c>.</summary>
    public PaginationOptions Counter(Func<PageInfo, string> text) => Set(() => CounterText = text);

    /// <summary>Replaces the counter button, for example with one that opens a "go to page" question.</summary>
    public PaginationOptions Counter(Func<PageInfo, BotButton?> button) => Set(() => CounterOverride = button);

    /// <summary>Sets the label of the current page in <see cref="PaginationStyle.Numbered"/>.</summary>
    public PaginationOptions CurrentPage(Func<PageInfo, string> text) => Set(() => CurrentPageText = text);

    /// <summary>Sets the label of placeholders (<see cref="PaginationEdge.Placeholder"/>).</summary>
    public PaginationOptions Placeholder(string text) => Set(() => PlaceholderText = Required(text));

    /// <summary>Chooses the buttons of the row.</summary>
    public PaginationOptions Style(PaginationStyle style) => Set(() => Look = style);

    /// <summary>Chooses what previous and next do on the first and last page.</summary>
    public PaginationOptions AtEdges(PaginationEdge edge) => Set(() => Edge = edge);

    /// <summary>Shows the row even when there is only one page. Hidden by default.</summary>
    public PaginationOptions ShowSinglePage(bool show = true) => Set(() => HideSinglePage = !show);

    /// <summary>
    /// Builds the row yourself from the standard buttons and your own, for example
    /// <c>p => [p.PreviousButton, p.CounterButton, p.NextButton, Button("🔍", "search")]</c>. <c>null</c> buttons are left out.
    /// </summary>
    public PaginationOptions Layout(Func<PageInfo, IEnumerable<BotButton?>> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Set(() => RowsOverride = page => [row(page)]);
    }

    /// <summary>
    /// Builds several rows yourself, for example <c>p => [[p.FirstButton, p.LastButton], [p.PreviousButton, p.CounterButton, p.NextButton]]</c>.
    /// </summary>
    public PaginationOptions LayoutRows(Func<PageInfo, IEnumerable<IEnumerable<BotButton?>>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return Set(() => RowsOverride = rows);
    }

    internal PaginationOptions Clone() => (PaginationOptions)MemberwiseClone();

    internal IEnumerable<IReadOnlyList<BotButton>> BuildRows(PageInfo page)
    {
        if (HideSinglePage && page.TotalPages <= 1)
            return [];

        var rows = RowsOverride?.Invoke(page) ?? new[] { DefaultRow(page) };
        return rows
            .Select(row => (IReadOnlyList<BotButton>)row.OfType<BotButton>().ToList())
            .Where(row => row.Count > 0)
            .ToList();
    }

    private IEnumerable<BotButton?> DefaultRow(PageInfo p) => Look switch
    {
        PaginationStyle.FirstLast => new[] { p.FirstButton, p.PreviousButton, p.CounterButton, p.NextButton, p.LastButton },
        PaginationStyle.Numbered => new[]
        {
            p.Page > 2 ? p.PageButton(1) : null,
            p.PreviousButton,
            p.Page > 1 ? p.PageButton(p.Page - 1) : null,
            p.PageButton(p.Page),
            p.Page < p.TotalPages ? p.PageButton(p.Page + 1) : null,
            p.NextButton,
            p.Page < p.TotalPages - 1 ? p.PageButton(p.TotalPages) : null
        },
        _ => new[] { p.PreviousButton, p.CounterButton, p.NextButton }
    };

    private PaginationOptions Set(Action change)
    {
        change();
        return this;
    }

    private static string Required(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return text;
    }
}

/// <summary>
/// The current page and the standard buttons, for overriding pagination buttons and layouts.
/// </summary>
public sealed class PageInfo
{
    private readonly Func<int, string> _route;
    private readonly PaginationOptions _options;
    private readonly bool _rich;

    internal PageInfo(int page, int totalPages, Func<int, string> route, PaginationOptions options, bool rich)
    {
        Page = page;
        TotalPages = totalPages;
        _route = route;
        _options = options;
        _rich = rich;
    }

    /// <summary>Gets the current page, starting at 1.</summary>
    public int Page { get; }

    /// <summary>Gets the number of pages, at least 1.</summary>
    public int TotalPages { get; }

    /// <summary>Gets a value indicating whether this is the first page.</summary>
    public bool IsFirst => Page <= 1;

    /// <summary>Gets a value indicating whether this is the last page.</summary>
    public bool IsLast => Page >= TotalPages;

    /// <summary>Gets the page the previous button goes to: the last page when on the first one (for <see cref="PaginationEdge.Wrap"/>).</summary>
    public int PreviousPage => IsFirst ? TotalPages : Page - 1;

    /// <summary>Gets the page the next button goes to: the first page when on the last one (for <see cref="PaginationEdge.Wrap"/>).</summary>
    public int NextPage => IsLast ? 1 : Page + 1;

    /// <summary>Gets the button data for a page, built with the route you passed.</summary>
    public string Route(int page) => _route(page);

    /// <summary>Gets the previous button, or <c>null</c> on the first page when it is hidden.</summary>
    public BotButton? PreviousButton => EdgeButton(IsFirst, _options.PreviousOverride, _options.PreviousText, PreviousPage);

    /// <summary>Gets the next button, or <c>null</c> on the last page when it is hidden.</summary>
    public BotButton? NextButton => EdgeButton(IsLast, _options.NextOverride, _options.NextText, NextPage);

    /// <summary>Gets the first-page button, or <c>null</c> on the first page when it is hidden.</summary>
    public BotButton? FirstButton => EdgeButton(IsFirst, _options.FirstOverride, _options.FirstText, 1, wraps: false);

    /// <summary>Gets the last-page button, or <c>null</c> on the last page when it is hidden.</summary>
    public BotButton? LastButton => EdgeButton(IsLast, _options.LastOverride, _options.LastText, TotalPages, wraps: false);

    /// <summary>Gets the counter, for example "2 / 5". Pressing it does nothing unless you replace it.</summary>
    public BotButton? CounterButton => _options.CounterOverride is { } counter
        ? counter(this)
        : Buttons.Callback(_options.CounterText(this));

    /// <summary>Gets the button of a page number; the current page does nothing when pressed.</summary>
    public BotButton PageButton(int page) => page == Page
        ? Buttons.Callback(_options.CurrentPageText(this))
        : Buttons.Callback(page.ToString(System.Globalization.CultureInfo.InvariantCulture), Route(page));

    private BotButton? EdgeButton(bool atEdge, Func<PageInfo, BotButton?>? custom, string text, int target, bool wraps = true)
    {
        if (atEdge)
        {
            switch (_options.Edge)
            {
                case PaginationEdge.Hide:
                case PaginationEdge.Wrap when !wraps:
                    return null;
                case PaginationEdge.Placeholder:
                    var placeholder = Buttons.Callback(_options.PlaceholderText);
                    return _rich ? placeholder.Disabled() : placeholder;
            }
        }

        return custom is not null ? custom(this) : Buttons.Callback(text, Route(target));
    }
}
