using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Client.Components.CommandPalette;
using BookmarkManager.Client.ComponentTests.TestDoubles;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class CommandPalettePerformanceTests
{
    private static (CommandPaletteService Palette, FakeBookmarkService Fake) Configure(BunitContext context)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var fake = new FakeBookmarkService();
        var palette = new CommandPaletteService();
        context.Services.AddSingleton<IBookmarkService>(fake);
        context.Services.AddSingleton<ICommandPaletteService>(palette);
        context.Services.AddSingleton(new KeyboardShortcutService());
        context.Services.AddSingleton(new PaletteFrecencyService(context.JSInterop.JSRuntime));
        context.Services.AddSingleton(new PaletteSearchHistoryService(context.JSInterop.JSRuntime));
        return (palette, fake);
    }

    private static BookmarkNodeDto Bookmark(string title, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Url = $"https://example.com/{title}",
        Type = NodeType.Bookmark,
        CreatedAt = createdAt,
        UpdatedAt = createdAt
    };

    private static TaskCompletionSource<PagedResult<BookmarkNodeDto>> Pending() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Skeleton_ShownWhileFirstFetchPending_ThenEmptyStateAfterZeroResults()
    {
        await using var context = new BunitContext();
        var (palette, fake) = Configure(context);
        var pending = Pending();
        fake.OnSearchBookmarks = (_, _) => pending.Task;

        var cut = context.Render<CommandPalette>();
        await cut.InvokeAsync(() => palette.Open());

        cut.WaitForElement(".palette-skeleton", TimeSpan.FromSeconds(3));
        Assert.Empty(cut.FindAll(".palette-no-results"));

        pending.SetResult(new PagedResult<BookmarkNodeDto> { Items = [], TotalCount = 0 });
        await cut.InvokeAsync(() => { }); // pump the renderer so the completion continuation runs

        Assert.Empty(cut.FindAll(".palette-skeleton"));
        Assert.NotEmpty(cut.FindAll(".palette-no-results"));
    }

    [Fact]
    public async Task SupersededRequest_DoesNotHideNewerSkeleton()
    {
        await using var context = new BunitContext();
        var (palette, fake) = Configure(context);

        var first = Pending();
        var second = Pending();
        var queue = new Queue<TaskCompletionSource<PagedResult<BookmarkNodeDto>>>();
        queue.Enqueue(first);
        queue.Enqueue(second);
        fake.OnSearchBookmarks = (_, _) => queue.Dequeue().Task;

        var cut = context.Render<CommandPalette>();
        await cut.InvokeAsync(() => palette.Open());
        cut.WaitForElement(".palette-skeleton", TimeSpan.FromSeconds(3));

        // Re-open without a debounce: supersedes the first in-flight request with a second one.
        await cut.InvokeAsync(() => cut.Instance.OpenPalette());
        Assert.Equal(2, fake.SearchRequests.Count);

        // The superseded request completes late; the newer request is still pending, so the
        // skeleton must not be cleared.
        first.SetResult(new PagedResult<BookmarkNodeDto> { Items = [Bookmark("old", DateTime.UtcNow)], TotalCount = 1 });
        await cut.InvokeAsync(() => { });
        Assert.NotEmpty(cut.FindAll(".palette-skeleton"));

        second.SetResult(new PagedResult<BookmarkNodeDto> { Items = [], TotalCount = 0 });
        await cut.InvokeAsync(() => { });
        Assert.Empty(cut.FindAll(".palette-skeleton"));
    }

    [Fact]
    public async Task Open_RequestsRecentlyAdded_SortByCreated_PageSize10_AndSelectsNewest()
    {
        await using var context = new BunitContext();
        var (palette, fake) = Configure(context);

        var newest = Bookmark("Newest", DateTime.UtcNow);
        var older = Bookmark("Older", DateTime.UtcNow.AddMinutes(-5));
        fake.OnSearchBookmarks = (request, _) => Task.FromResult(new PagedResult<BookmarkNodeDto>
        {
            Items = request.Page == 1 ? [newest, older] : [],
            TotalCount = 2
        });

        var cut = context.Render<CommandPalette>();
        await cut.InvokeAsync(() => palette.Open());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Recently added", cut.Find(".palette-header-title").TextContent.Trim());
            Assert.Equal("Recently added", cut.Find(".palette-section-title").TextContent.Trim());

            var firstItem = cut.Find(".palette-item:not(.palette-section-header)");
            Assert.Contains("Newest", firstItem.TextContent);
            Assert.Contains("is-active", firstItem.ClassName);
        }, TimeSpan.FromSeconds(3));

        var request = Assert.Single(fake.SearchRequests);
        Assert.Equal("Created", request.SortBy);
        Assert.Equal(10, request.PageSize);
        Assert.Equal(string.Empty, request.Query);
    }

    [Fact]
    public async Task Enter_OnRecentlyAddedNewest_TriggersPrimaryNavigation()
    {
        await using var context = new BunitContext();
        var (palette, fake) = Configure(context);

        var newest = Bookmark("Newest", DateTime.UtcNow);
        fake.OnSearchBookmarks = (_, _) => Task.FromResult(new PagedResult<BookmarkNodeDto>
        {
            Items = [newest],
            TotalCount = 1
        });

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<CommandPalette>();
        await cut.InvokeAsync(() => palette.Open());

        cut.WaitForAssertion(
            () => Assert.Contains("Newest", cut.Find(".palette-item:not(.palette-section-header)").TextContent),
            TimeSpan.FromSeconds(3));

        await cut.InvokeAsync(() => cut.Instance.ExecutePrimary());

        Assert.Contains(newest.Id.ToString(), navigation.Uri);
    }

    [Fact]
    public async Task Open_ShowsRecentlyAddedThenFolders_AndDoesNotPageTheOpenView()
    {
        await using var context = new BunitContext();
        var (palette, fake) = Configure(context);

        fake.FolderTree = [new FolderTreeNodeDto { Id = Guid.NewGuid(), Title = "Novels" }];
        var page1 = Enumerable.Range(0, 10).Select(i => Bookmark($"b{i}", DateTime.UtcNow.AddMinutes(-i))).ToList();
        fake.OnSearchBookmarks = (_, _) => Task.FromResult(new PagedResult<BookmarkNodeDto>
        {
            Items = page1,
            TotalCount = 25
        });

        var cut = context.Render<CommandPalette>();
        await cut.InvokeAsync(() => palette.Open());

        cut.WaitForAssertion(() =>
        {
            var sections = cut.FindAll(".palette-section-title").Select(e => e.TextContent.Trim()).ToList();
            Assert.Equal(["Recently added", "Folders"], sections);
            Assert.Contains(cut.FindAll(".palette-item"), e => e.TextContent.Contains("Novels"));
        }, TimeSpan.FromSeconds(3));

        // Paging the recent list would push the folders out of reach, so scrolling fetches nothing.
        await cut.InvokeAsync(() => cut.Instance.LoadMoreFromScroll());
        Assert.Single(fake.SearchRequests);
        Assert.Empty(cut.FindAll(".palette-load-more"));
    }

    [Fact]
    public async Task TypedSearch_UsesPageSize10()
    {
        await using var context = new BunitContext();
        var (palette, fake) = Configure(context);
        fake.OnSearchBookmarks = (_, _) => Task.FromResult(new PagedResult<BookmarkNodeDto>
        {
            Items = [Bookmark("Series", DateTime.UtcNow)],
            TotalCount = 1
        });

        var cut = context.Render<CommandPalette>();
        await cut.InvokeAsync(() => palette.Open());
        cut.WaitForAssertion(() => cut.Find("#paletteSearchInput"), TimeSpan.FromSeconds(3));

        cut.Find("#paletteSearchInput").Input("Series");

        cut.WaitForAssertion(
            () => Assert.Contains(fake.SearchRequests, r => r.Query == "Series"),
            TimeSpan.FromSeconds(3));

        var searchRequest = fake.SearchRequests.Single(r => r.Query == "Series");
        Assert.Equal(10, searchRequest.PageSize);
        Assert.Null(searchRequest.SortBy);
    }

    [Fact]
    public async Task FolderTree_FetchedOncePerSession_AndAgainAfterReopen()
    {
        await using var context = new BunitContext();
        var (palette, fake) = Configure(context);
        fake.OnSearchBookmarks = (_, _) => Task.FromResult(new PagedResult<BookmarkNodeDto>
        {
            Items = [],
            TotalCount = 0
        });

        var cut = context.Render<CommandPalette>();
        await cut.InvokeAsync(() => palette.Open());
        cut.WaitForAssertion(() => Assert.Equal(1, fake.GetFolderTreeCallCount), TimeSpan.FromSeconds(3));

        cut.Find("#paletteSearchInput").Input("a");
        cut.WaitForAssertion(
            () => Assert.Contains(fake.SearchRequests, r => r.Query == "a"),
            TimeSpan.FromSeconds(3));

        cut.Find("#paletteSearchInput").Input("ab");
        cut.WaitForAssertion(
            () => Assert.Contains(fake.SearchRequests, r => r.Query == "ab"),
            TimeSpan.FromSeconds(3));

        Assert.Equal(1, fake.GetFolderTreeCallCount);

        await cut.InvokeAsync(() => cut.Instance.OpenPalette());
        cut.WaitForAssertion(() => Assert.Equal(2, fake.GetFolderTreeCallCount), TimeSpan.FromSeconds(3));
    }
}
