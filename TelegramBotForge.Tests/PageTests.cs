using Telegram.Bot.Types.ReplyMarkups;
using TelegramBotForge.Views;

namespace TelegramBotForge.Tests;

public sealed record PageRequest(IReadOnlyList<int> Items, int Page, int PageSize = 5, Action<PaginationOptions>? Configure = null);

/// <summary>The existing overload: one button per item.</summary>
public sealed class ButtonListView : View<PageRequest>
{
    protected override void Build(MessageBuilder message, PageRequest model) => message
        .Line($"List")
        .Page(model.Items, model.Page, model.PageSize,
            item: i => Button($"Item {i}", $"item:{i}"),
            route: p => $"list:{p}",
            configure: model.Configure);
}

/// <summary>The new overload doing what the existing one does.</summary>
public sealed class RenderedButtonListView : View<PageRequest>
{
    protected override void Build(MessageBuilder message, PageRequest model) => message
        .Line($"List")
        .Page(model.Items, model.Page, model.PageSize,
            renderPage: (body, visible, _) => body.Buttons(visible, i => Button($"Item {i}", $"item:{i}")),
            route: p => $"list:{p}",
            configure: model.Configure);
}

/// <summary>Text, several buttons per item, and what the renderer saw.</summary>
public sealed class DetailedListView : View<PageRequest>
{
    public static List<(IReadOnlyList<int> Visible, PageInfo Info)> Calls { get; } = [];

    protected override void Build(MessageBuilder message, PageRequest model) => message
        .Line($"<b>Orders</b>")
        .Page(model.Items, model.Page, model.PageSize,
            renderPage: (body, visible, info) =>
            {
                lock (Calls)
                    Calls.Add((visible, info));
                if (visible.Count == 0)
                {
                    body.Line($"No orders yet.");
                    return;
                }

                body.Line($"Orders {info.FirstItemNumber}–{info.LastItemNumber} of {info.TotalItems}");
                foreach (var order in visible)
                    body.Row(Button($"#{order}", $"order:{order}"), Button("❌", $"cancel:{order}"));
            },
            route: p => $"orders:{p}",
            configure: model.Configure);
}

/// <summary>A rich message with a whole HTML table per page.</summary>
[View(Rich = true)]
public sealed class TableView : View<PageRequest>
{
    protected override void Build(MessageBuilder message, PageRequest model) => message
        .Page(model.Items, model.Page, model.PageSize,
            renderPage: (body, visible, info) => body.Line(Raw(
                "<table><tr><th>#</th><th>Square</th></tr>" +
                string.Concat(visible.Select(i => $"<tr><td>{i}</td><td>{i * i}</td></tr>")) +
                "</table>")),
            route: p => $"table:{p}");
}

/// <summary>Pagination options of the view apply to the new overload too.</summary>
public sealed class NumberedListView : View<PageRequest>
{
    protected override void ConfigurePagination(PaginationOptions options) => options.Style(PaginationStyle.Numbered);

    protected override void Build(MessageBuilder message, PageRequest model) => message
        .Line($"List")
        .Page(model.Items, model.Page, model.PageSize,
            renderPage: (body, visible, _) => body.Buttons(visible, i => Button($"{i}", $"n:{i}"), perRow: 5),
            route: p => $"n:{p}",
            configure: model.Configure);
}

public sealed class PageTests
{
    private static readonly int[] Twelve = Enumerable.Range(1, 12).ToArray();

    private static List<List<string>> Rows(BotMessage message) =>
        (message.Keyboard as InlineKeyboardMarkup)?.InlineKeyboard
            .Select(row => row.Select(button => button.CallbackData is { } data and not "tbf:noop" ? $"{button.Text}={data}" : button.Text).ToList())
            .ToList() ?? [];

    private static (IReadOnlyList<int> Visible, PageInfo Info) Render(PageRequest request)
    {
        lock (DetailedListView.Calls)
        {
            DetailedListView.Calls.Clear();
            new DetailedListView().Render(request);
            return DetailedListView.Calls.Single();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(0)]
    [InlineData(99)]
    public void Existing_overload_is_unchanged_and_matches_the_new_one(int page)
    {
        var request = new PageRequest(Twelve, page);
        var existing = new ButtonListView().Render(request);
        var rendered = new RenderedButtonListView().Render(request);

        Assert.Equal(Rows(existing), Rows(rendered));
        Assert.Equal(existing.Text, rendered.Text);
    }

    [Fact]
    public void Existing_overload_shows_the_page_and_the_row()
    {
        var rows = Rows(new ButtonListView().Render(new PageRequest(Twelve, 2)));

        Assert.Equal(
        [
            ["Item 6=item:6"], ["Item 7=item:7"], ["Item 8=item:8"], ["Item 9=item:9"], ["Item 10=item:10"],
            ["«=list:1", "2 / 3", "»=list:3"]
        ], rows);
    }

    [Fact]
    public void Empty_list_has_one_empty_page_and_no_row()
    {
        var message = new DetailedListView().Render(new PageRequest([], 3));
        var (visible, info) = Render(new PageRequest([], 3));

        Assert.Empty(visible);
        Assert.Equal(1, info.Page);
        Assert.Equal(1, info.TotalPages);
        Assert.Equal(0, info.TotalItems);
        Assert.Null(info.FirstItemNumber);
        Assert.Null(info.LastItemNumber);
        Assert.Contains("No orders yet.", message.Text);
        Assert.Empty(Rows(message));

        // As before: the existing overload shows no buttons for an empty list.
        Assert.Null(new ButtonListView().Render(new PageRequest([], 1)).Keyboard);
    }

    [Fact]
    public void Empty_list_shows_the_row_when_asked()
    {
        var message = new DetailedListView().Render(new PageRequest([], 1, Configure: o => o.ShowSinglePage()));
        Assert.Equal([["1 / 1"]], Rows(message));
    }

    [Theory]
    [InlineData(1, 1, new[] { 1, 2, 3, 4, 5 }, 1, 5)]
    [InlineData(2, 2, new[] { 6, 7, 8, 9, 10 }, 6, 10)]
    [InlineData(3, 3, new[] { 11, 12 }, 11, 12)]
    [InlineData(0, 1, new[] { 1, 2, 3, 4, 5 }, 1, 5)]
    [InlineData(-7, 1, new[] { 1, 2, 3, 4, 5 }, 1, 5)]
    [InlineData(4, 3, new[] { 11, 12 }, 11, 12)]
    [InlineData(int.MaxValue, 3, new[] { 11, 12 }, 11, 12)]
    public void Pages_are_one_based_and_clamped(int requested, int expectedPage, int[] expectedItems, int first, int last)
    {
        var (visible, info) = Render(new PageRequest(Twelve, requested));

        Assert.Equal(expectedItems, visible);
        Assert.Equal(expectedPage, info.Page);
        Assert.Equal(3, info.TotalPages);
        Assert.Equal(12, info.TotalItems);
        Assert.Equal(5, info.PageSize);
        Assert.Equal(first, info.FirstItemNumber);
        Assert.Equal(last, info.LastItemNumber);
        Assert.Equal(expectedPage == 1, info.IsFirst);
        Assert.Equal(expectedPage == 3, info.IsLast);
    }

    [Fact]
    public void Exactly_full_pages_have_no_empty_last_page()
    {
        var (visible, info) = Render(new PageRequest(Enumerable.Range(1, 10).ToArray(), 2));
        Assert.Equal(2, info.TotalPages);
        Assert.Equal([6, 7, 8, 9, 10], visible);
    }

    [Fact]
    public void One_page_hides_the_row()
    {
        var message = new DetailedListView().Render(new PageRequest([1, 2, 3], 1));
        Assert.Equal([["#1=order:1", "❌=cancel:1"], ["#2=order:2", "❌=cancel:2"], ["#3=order:3", "❌=cancel:3"]], Rows(message));
    }

    [Fact]
    public void Custom_content_comes_before_the_navigation()
    {
        var message = new DetailedListView().Render(new PageRequest(Twelve, 3));

        Assert.Equal("<b>Orders</b>\nOrders 11–12 of 12", message.Text);
        Assert.Equal(
            [["#11=order:11", "❌=cancel:11"], ["#12=order:12", "❌=cancel:12"], ["«=orders:2", "3 / 3"]],
            Rows(message));
    }

    [Fact]
    public void Pagination_options_of_the_call_apply()
    {
        var message = new DetailedListView().Render(new PageRequest(Twelve, 2, Configure: o => o
            .Style(PaginationStyle.FirstLast)
            .Counter(p => $"Page {p.Page} of {p.TotalPages}")));

        Assert.Equal(["⏮=orders:1", "«=orders:1", "Page 2 of 3", "»=orders:3", "⏭=orders:3"],
            Rows(message).Last());
    }

    [Fact]
    public void Pagination_options_can_wrap_and_use_placeholders()
    {
        var wrap = new DetailedListView().Render(new PageRequest(Twelve, 1, Configure: o => o.AtEdges(PaginationEdge.Wrap)));
        Assert.Equal(["«=orders:3", "1 / 3", "»=orders:2"], Rows(wrap).Last());

        var placeholder = new DetailedListView().Render(new PageRequest(Twelve, 3, Configure: o => o.AtEdges(PaginationEdge.Placeholder)));
        Assert.Equal(["«=orders:2", "3 / 3", "·"], Rows(placeholder).Last());
    }

    [Fact]
    public void Pagination_options_of_the_view_apply()
    {
        var message = new NumberedListView().Render(new PageRequest(Enumerable.Range(1, 50).ToArray(), 4));
        Assert.Equal(["1=n:1", "«=n:3", "3=n:3", "[4]", "5=n:5", "»=n:5", "10=n:10"],
            Rows(message).Last());
    }

    [Fact]
    public void Rich_message_can_show_a_table_per_page()
    {
        var message = new TableView().Render(new PageRequest(Twelve, 2));

        Assert.True(message.Rich);
        Assert.StartsWith("<table><tr><th>#</th><th>Square</th></tr><tr><td>6</td><td>36</td></tr>", message.Text);
        Assert.Contains("<tr><td>10</td><td>100</td></tr></table>", message.Text);
        Assert.DoesNotContain("<td>11</td>", message.Text);

        // The navigation of a rich message is part of its HTML, after the table.
        var table = message.Text.IndexOf("</table>", StringComparison.Ordinal);
        var navigation = message.Text.IndexOf("<tg-button-row>", StringComparison.Ordinal);
        Assert.True(navigation > table, message.Text);
        Assert.Contains("data=\"table:3\"", message.Text);
    }

    [Fact]
    public void Arguments_are_checked_like_the_existing_overload()
    {
        var builder = new MessageBuilder("Test", rich: false, _ => { });
        Action<MessageBuilder, IReadOnlyList<int>, PageInfo> render = (_, _, _) => { };

        Assert.Throws<ArgumentNullException>(() => builder.Page<int>(null!, 1, 5, render, p => $"{p}"));
        Assert.Throws<ArgumentNullException>(() => builder.Page(Twelve, 1, 5, renderPage: null!, p => $"{p}"));
        Assert.Throws<ArgumentNullException>(() => builder.Page(Twelve, 1, 5, render, route: null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Page(Twelve, 1, 0, render, p => $"{p}"));

        Assert.Throws<ArgumentNullException>(() => builder.Page<int>(null!, 1, 5, item: i => Buttons.Callback($"{i}", "x"), p => $"{p}"));
        Assert.Throws<ArgumentNullException>(() => builder.Page(Twelve, 1, 5, item: null!, p => $"{p}"));
        Assert.Throws<ArgumentNullException>(() => builder.Page(Twelve, 1, 5, i => Buttons.Callback($"{i}", "x"), route: null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Page(Twelve, 1, 0, i => Buttons.Callback($"{i}", "x"), p => $"{p}"));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Page(Twelve, 1, 5, i => Buttons.Callback($"{i}", "x"), p => $"{p}", perRow: 0));
    }

    [Fact]
    public void Pagination_without_a_list_has_no_item_numbers()
    {
        PageInfo? seen = null;
        new MessageBuilder("Test", rich: false, _ => { })
            .Line($"x")
            .Pagination(2, 4, p => $"p:{p}", o => o.Layout(p =>
            {
                seen = p;
                return [p.CounterButton];
            }));

        Assert.NotNull(seen);
        Assert.Null(seen.TotalItems);
        Assert.Null(seen.PageSize);
        Assert.Null(seen.FirstItemNumber);
    }

    [Theory]
    [InlineData(0, 1, 5, 1, 1, 0, 0)]
    [InlineData(12, 1, 5, 1, 3, 0, 5)]
    [InlineData(12, 3, 5, 3, 3, 10, 2)]
    [InlineData(12, 9, 5, 3, 3, 10, 2)]
    [InlineData(10, 2, 5, 2, 2, 5, 5)]
    [InlineData(int.MaxValue, 2, int.MaxValue, 1, 1, 0, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue, 1, int.MaxValue, int.MaxValue, int.MaxValue - 1, 1)]
    public void Page_window_counts_without_overflow(int count, int page, int size, int expectedPage, int totalPages, int skip, int take)
    {
        Assert.Equal(new PageWindow(expectedPage, totalPages, skip, take), PageWindow.Of(count, page, size));
    }
}
