using BookmarkManager.Client.Features.Bookmarks.Components;
using BookmarkManager.Contracts;
using Bunit;
using MudBlazor;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarkCardPreviewTests
{
    private static readonly TimeSpan PreviewWaitTimeout = TimeSpan.FromSeconds(2);

    private static BookmarkNodeDto MakeBookmark(
        string title = "Solo Leveling Chapter 179",
        string url = "https://example.com/manga/solo-leveling/chapter-179",
        string? category = "Manga",
        string[]? tags = null)
    {
        return new BookmarkNodeDto
        {
            Id = Guid.NewGuid(),
            Type = NodeType.Bookmark,
            Title = title,
            Url = url,
            UpdatedAt = new DateTime(2026, 6, 1, 12, 30, 0, DateTimeKind.Utc),
            Metadata = new BookmarkMetadataDto
            {
                Category = category,
                Tags = (tags ?? ["Action", "Isekai"]).ToList()
            }
        };
    }

    // MudPopover (Phase 7 hover preview) portals its content to a mounted
    // MudPopoverProvider rather than rendering it inside BookmarkCard's own
    // render tree, so assertions on the *popover content* must search the
    // provider's rendered output, not the card's. Both are rendered as
    // separate bUnit roots here (mirrors how MainLayout mounts the provider
    // once and cards render underneath it in production).
    private sealed record PreviewTestHost(
        BunitContext Context,
        IRenderedComponent<MudPopoverProvider> Provider,
        IRenderedComponent<BookmarkCard> Card) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private static PreviewTestHost NewContext(BookmarkNodeDto item, bool withFolderPath = true)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var provider = context.Render<MudPopoverProvider>();
        var card = context.Render<BookmarkCard>(parameters =>
        {
            parameters.Add(p => p.Item, item);
            parameters.Add(p => p.FormatUpdatedAt, (DateTime dt) => dt.ToString("MMM d"));
            if (withFolderPath)
            {
                parameters.Add(p => p.GetFolderPath, (Guid? _) => "Bookmarks bar / Manga");
            }
        });

        return new PreviewTestHost(context, provider, card);
    }

    [Fact]
    public async Task Renders_WithFullMetadata_WithoutThrowing_AndShowsTitleAndUrl()
    {
        var item = MakeBookmark();
        await using var host = NewContext(item);

        Assert.Contains(item.Title, host.Card.Markup);
        Assert.Contains(item.Url!, host.Card.Markup);
    }

    [Fact]
    public async Task Hover_PastDelay_OpensPreviewPopover_WithTitleUrlTagsAndFolderPath()
    {
        var item = MakeBookmark(category: "Manga", tags: ["Action", "Isekai"]);
        await using var host = NewContext(item);

        var card = host.Card.Find($"#bookmark-card-{item.Id}");
        card.MouseEnter();

        host.Provider.WaitForAssertion(
            () => Assert.NotEmpty(host.Provider.FindAll(".bookmark-preview-content")),
            timeout: PreviewWaitTimeout);

        var previewMarkup = host.Provider.Find(".bookmark-preview-content").InnerHtml;
        Assert.Contains(item.Title, previewMarkup);
        Assert.Contains(item.Url!, previewMarkup);
        Assert.Contains("Manga", previewMarkup);
        Assert.Contains("Action", previewMarkup);
        Assert.Contains("Isekai", previewMarkup);
        Assert.Contains("Bookmarks bar / Manga", previewMarkup);
    }

    [Fact]
    public async Task MouseLeave_BeforeDelayElapses_NeverOpensPreview()
    {
        var item = MakeBookmark();
        await using var host = NewContext(item);

        var card = host.Card.Find($"#bookmark-card-{item.Id}");
        card.MouseEnter();
        card.MouseLeave();

        // Give the (cancelled) 400ms delay task time to have run if it were
        // ever going to — the preview must stay closed since the pointer left
        // the card before the hover delay elapsed.
        await Task.Delay(600);

        Assert.Empty(host.Provider.FindAll(".bookmark-preview-content"));
    }

    [Fact]
    public async Task MouseLeave_AfterPreviewOpened_ClosesPreview()
    {
        var item = MakeBookmark();
        await using var host = NewContext(item);

        var card = host.Card.Find($"#bookmark-card-{item.Id}");
        card.MouseEnter();

        host.Provider.WaitForAssertion(
            () => Assert.NotEmpty(host.Provider.FindAll(".bookmark-preview-content")),
            timeout: PreviewWaitTimeout);

        card = host.Card.Find($"#bookmark-card-{item.Id}");
        card.MouseLeave();

        host.Provider.WaitForAssertion(
            () => Assert.Empty(host.Provider.FindAll(".bookmark-preview-content")),
            timeout: PreviewWaitTimeout);
    }

    [Fact]
    public async Task Hover_OnFolder_DoesNotShowUrl_ButShowsFolderPath()
    {
        var item = new BookmarkNodeDto
        {
            Id = Guid.NewGuid(),
            Type = NodeType.Folder,
            Title = "Manga",
            UpdatedAt = new DateTime(2026, 6, 1, 12, 30, 0, DateTimeKind.Utc)
        };
        await using var host = NewContext(item);

        var card = host.Card.Find($"#bookmark-card-{item.Id}");
        card.MouseEnter();

        host.Provider.WaitForAssertion(
            () => Assert.NotEmpty(host.Provider.FindAll(".bookmark-preview-content")),
            timeout: PreviewWaitTimeout);

        Assert.Empty(host.Provider.FindAll(".bookmark-preview-url"));
        Assert.Contains("Bookmarks bar / Manga", host.Provider.Find(".bookmark-preview-content").InnerHtml);
    }
}
