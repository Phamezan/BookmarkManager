using BookmarkManager.Client.Features.Bookmarks.Components;
using BookmarkManager.Contracts;
using Bunit;
using MudBlazor;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarkListTests
{
    [Fact]
    public async Task UpdateRows_PreservesCallerOrder_DoesNotReSortFoldersFirst()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        // BookmarkCard's hover-preview popover (Phase 7) needs a mounted MudPopoverProvider.
        context.Render<MudPopoverProvider>();

        // Caller (Bookmarks.VisibleItems) is the single owner of ordering; here we
        // deliberately pass folders first (as VisibleItems would) followed by bookmarks,
        // with titles that would sort differently if BookmarkList re-sorted internally.
        var folder = new BookmarkNodeDto { Id = Guid.NewGuid(), Title = "Z Folder", Type = NodeType.Folder };
        var bookmarkA = new BookmarkNodeDto { Id = Guid.NewGuid(), Title = "A Bookmark", Type = NodeType.Bookmark, Url = "https://example.com/a" };
        var bookmarkB = new BookmarkNodeDto { Id = Guid.NewGuid(), Title = "B Bookmark", Type = NodeType.Bookmark, Url = "https://example.com/b" };
        var items = new List<BookmarkNodeDto> { folder, bookmarkA, bookmarkB };

        var component = context.Render<BookmarkList>(parameters => parameters
            .Add(p => p.Items, items));

        var cardIds = component.FindAll(".bookmark-card")
            .Select(el => el.GetAttribute("id"))
            .ToList();

        Assert.Equal(
            new[] { $"bookmark-card-{folder.Id}", $"bookmark-card-{bookmarkA.Id}", $"bookmark-card-{bookmarkB.Id}" },
            cardIds);
    }

    [Fact]
    public async Task UpdateRows_MixedFolderBookmarkOrder_KeepsExactInputOrder()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        // BookmarkCard's hover-preview popover (Phase 7) needs a mounted MudPopoverProvider.
        context.Render<MudPopoverProvider>();

        // Even an unusual (interleaved) order supplied by the caller must be preserved —
        // BookmarkList must not impose its own folders-first ordering anymore.
        var bookmark = new BookmarkNodeDto { Id = Guid.NewGuid(), Title = "Bookmark", Type = NodeType.Bookmark, Url = "https://example.com" };
        var folder = new BookmarkNodeDto { Id = Guid.NewGuid(), Title = "Folder", Type = NodeType.Folder };
        var items = new List<BookmarkNodeDto> { bookmark, folder };

        var component = context.Render<BookmarkList>(parameters => parameters
            .Add(p => p.Items, items));

        var cardIds = component.FindAll(".bookmark-card")
            .Select(el => el.GetAttribute("id"))
            .ToList();

        Assert.Equal(
            new[] { $"bookmark-card-{bookmark.Id}", $"bookmark-card-{folder.Id}" },
            cardIds);
    }
}
