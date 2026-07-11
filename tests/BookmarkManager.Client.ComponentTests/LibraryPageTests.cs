using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Client.ComponentTests.TestDoubles;
using BookmarkManager.Client.Pages;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace BookmarkManager.Client.ComponentTests;

public sealed class LibraryPageTests
{
    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage(BunitContext context)
    {
        return context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudBlazor.MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<Library>(3);
            builder.CloseComponent();
        });
    }

    [Fact]
    public async Task Library_LoadsTrendingOnInit()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var fake = new FakeLibraryService();
        fake.Trending.Add(MakeEntry("AniList", "1", "Frieren"));
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.Contains("Frieren", page.Markup));
        Assert.True(fake.TrendingCallCount > 0);
        Assert.Equal(0, fake.SearchCallCount);
    }

    [Fact]
    public async Task Library_TypingSearchDebouncesThenCallsSearchService()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var fake = new FakeLibraryService();
        fake.SearchResults.Add(MakeEntry("MangaDex", "2", "Solo Leveling"));
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);
        page.WaitForAssertion(() => Assert.True(fake.TrendingCallCount > 0));

        var input = page.Find(".lib-search-input");
        input.Input("solo leveling");

        page.WaitForAssertion(() => Assert.Contains("Solo Leveling", page.Markup), TimeSpan.FromSeconds(2));
        Assert.True(fake.SearchCallCount > 0);
        Assert.Equal("solo leveling", fake.LastSearchQuery);
    }

    [Fact]
    public async Task Library_MoreButton_OpensDetailsDialog()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var fake = new FakeLibraryService();
        fake.Trending.Add(MakeEntry("AniList", "1", "Frieren"));
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".lib-more-btn")));

        page.Find(".lib-more-btn").Click();

        page.WaitForAssertion(() => Assert.Contains("Frieren", page.Markup));
        page.WaitForAssertion(() => Assert.Contains("Open on AniList", page.Markup));
    }

    [Fact]
    public async Task Library_LoadMore_AppendsNextPageAndHidesButtonWhenExhausted()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var fake = new FakeLibraryService();
        for (var i = 0; i < 50; i++)
        {
            fake.Trending.Add(MakeEntry("AniList", i.ToString(), $"Title {i}"));
        }
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.Equal(48, page.FindAll(".lib-card").Count));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".lib-load-more-btn")));

        page.Find(".lib-load-more-btn").Click();

        page.WaitForAssertion(() => Assert.Equal(50, page.FindAll(".lib-card").Count));
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".lib-load-more-btn")));
    }

    [Fact]
    public async Task Library_SortingDropdown_ChangesSortOrder()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var fake = new FakeLibraryService();
        // Item A: Rating 7.0, Title "B_Title", LastReleaseAt: 2 days ago
        fake.Trending.Add(new LibraryEntryDto("AniList", "1", "B_Title", [], [], LibraryMediaType.Manga, null, "Synopsis", ["Fantasy"], 7.0, "Releasing", "10", null, DateTimeOffset.UtcNow.AddDays(-2), "https://example.com/1"));
        // Item B: Rating 9.0, Title "A_Title", LastReleaseAt: 1 day ago
        fake.Trending.Add(new LibraryEntryDto("AniList", "2", "A_Title", [], [], LibraryMediaType.Manga, null, "Synopsis", ["Fantasy"], 9.0, "Releasing", "10", null, DateTimeOffset.UtcNow.AddDays(-1), "https://example.com/2"));
        
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".lib-card").Count));

        // Default sort is New & trending (recency, then rating). A_Title is newer and higher rated.
        var cardTitles = page.FindAll(".lib-card-title").Select(el => el.TextContent.Trim()).ToList();
        Assert.Equal(new[] { "A_Title", "B_Title" }, cardTitles);

        // Click sort dropdown activator button to open menu
        var sortBtn = page.Find(".lib-sort-btn");
        sortBtn.Click();

        // Wait for popover / MudMenuItems to render
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".lib-sort-item")));

        // Let's click "A – Z" menu item.
        var sortItems = page.FindAll(".lib-sort-item");
        var alphaSortItem = sortItems.First(el => el.TextContent.Contains("A – Z"));
        alphaSortItem.Click();

        // Check that _sort was set and the label changed
        page.WaitForAssertion(() => Assert.Contains("A – Z", page.Find(".lib-sort-btn-label").TextContent));
    }

    [Fact]
    public async Task Library_Spotlight_RecommendsShowsCardGridWithShuffle()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var fake = new FakeLibraryService();
        for (var i = 0; i < 20; i++)
            fake.Trending.Add(MakeEntry("Novelfire", i.ToString(), $"Rec {i}"));
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);
        page.WaitForAssertion(() => Assert.Contains("Trending this week", page.Markup));

        var recommendsTab = page.FindAll(".lib-spotlight-tab").First(el => el.TextContent.Contains("Recommends"));
        recommendsTab.Click();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".lib-recommends-grid .lib-card")));
        Assert.Empty(page.FindAll(".lib-hero-content"));
        page.Find(".lib-recommends-shuffle").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".lib-recommends-grid .lib-card")));
    }

    [Fact]
    public async Task Library_RecommendsTypeFilter_LoadsTypeSpecificPool()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var fake = new FakeLibraryService();
        fake.Trending.Add(MakeEntry("Novelfire", "1", "Manga Pick", LibraryMediaType.Manga));
        fake.Trending.Add(MakeEntry("Novelfire", "2", "Webnovel Pick", LibraryMediaType.Webnovel));
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);
        page.WaitForAssertion(() => Assert.Contains("Trending this week", page.Markup));

        page.FindAll(".lib-spotlight-tab").First(el => el.TextContent.Contains("Recommends")).Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".lib-recommends-grid .lib-card")));

        var webnovelTab = page.FindAll(".lib-recommends-tab").First(el => el.TextContent.Contains("Webnovel"));
        webnovelTab.Click();

        page.WaitForAssertion(() => Assert.Contains("Webnovel", fake.LastTrendingMediaType?.ToString() ?? ""));
        page.WaitForAssertion(() => Assert.Contains("Webnovel Pick", page.Markup));
    }

    [Fact]
    public async Task Library_GenrePanel_GroupsTagsAndExpands()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var fake = new FakeLibraryService();
        fake.Trending.Add(new LibraryEntryDto(
            "Novelfire", "1", "Sample", [], [], LibraryMediaType.Webnovel, null, "Synopsis",
            ["Action", "Isekai", "Romance", "Cyberpunk", "Zombie Apocalypse", "Comedy", "Horror", "Mystery"],
            8.0, "Ongoing", "1", null, DateTimeOffset.UtcNow, "https://example.com/1"));
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".lib-genre-group")));

        var groupLabels = page.FindAll(".lib-genre-group-label").Select(el => el.TextContent.Trim()).ToList();
        Assert.Contains(groupLabels, label => label.Contains("Action", StringComparison.OrdinalIgnoreCase));

        var expandBtn = page.Find(".lib-genre-expand-btn");
        expandBtn.Click();
        page.WaitForAssertion(() => Assert.Contains("Show fewer tags", expandBtn.TextContent));

        groupLabels = page.FindAll(".lib-genre-group-label").Select(el => el.TextContent.Trim()).ToList();
        Assert.Contains(groupLabels, label => label.Contains("Other tags", StringComparison.OrdinalIgnoreCase));
    }


    [Fact]
    public async Task Library_ReadingProgress_RendersBadgeAndStatusOnMatchedCard()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var fake = new FakeLibraryService();
        fake.Trending.Add(MakeEntry("AniList", "1", "Frieren"));
        fake.ReadingProgress.Add(new LibraryReadingProgressDto("AniList", "1", 12, null, 28));
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.Contains("12/28", page.Markup), TimeSpan.FromSeconds(2));
        page.WaitForAssertion(() => Assert.Contains("Ongoing", page.Markup), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Library_MatchedProgress_RendersClickableBadgeAndHeroBookmarkAction()
    {
        var bookmarkId = Guid.NewGuid();
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var fake = new FakeLibraryService();
        fake.Trending.Add(MakeEntry("AniList", "1", "Frieren"));
        fake.ReadingProgress.Add(new LibraryReadingProgressDto(
            "AniList", "1", 12, null, 28, bookmarkId, "Frieren Ch. 12", "https://example.com/frieren/12"));
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.Contains("12/28 · yours", page.Markup), TimeSpan.FromSeconds(2));
        page.WaitForAssertion(() => Assert.Contains("Go to bookmark", page.Markup), TimeSpan.FromSeconds(2));

        var badge = page.Find(".lib-card-progress.is-action");
        Assert.Equal("Go to bookmark (Ctrl+K for command palette)", badge.GetAttribute("title"));

        badge.Click();

        var nav = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Assert.Contains($"bookmarkId={bookmarkId}", nav.Uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Library_DetailsDialog_ShowsGoToBookmarkWhenMatched()
    {
        var bookmarkId = Guid.NewGuid();
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var fake = new FakeLibraryService();
        fake.Trending.Add(MakeEntry("AniList", "1", "Frieren"));
        fake.ReadingProgress.Add(new LibraryReadingProgressDto(
            "AniList", "1", 12, null, 28, bookmarkId, "Frieren Ch. 12", "https://example.com/frieren/12"));
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".lib-more-btn")), TimeSpan.FromSeconds(2));

        page.Find(".lib-more-btn").Click();

        page.WaitForAssertion(() => Assert.Contains("Go to Bookmark", page.Markup), TimeSpan.FromSeconds(2));
        page.WaitForAssertion(() => Assert.Contains("Your Progress", page.Markup), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Library_MyBookmarksToggle_FiltersToMatchedSeriesOnly()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var fake = new FakeLibraryService();
        // "Buried Series" is deliberately NOT in Trending - it's a matched bookmark that isn't
        // on the currently loaded trending page, which is the exact case the "My bookmarks"
        // filter must still surface (it has its own item source, not a narrowing of ActiveItems).
        fake.Trending.Add(MakeEntry("AniList", "2", "Unmatched Series"));
        fake.ReadingProgress.Add(new LibraryReadingProgressDto("MangaDex", "buried-1", 12, null, 28));
        fake.MyBookmarkedSeries.Add(MakeEntry("MangaDex", "buried-1", "Buried Series"));
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".lib-card")));
        Assert.Contains("Unmatched Series", page.Markup);

        page.Find(".lib-bookmarks-tab").Click();

        page.WaitForAssertion(() => Assert.Contains("Buried Series", page.Markup), TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("Unmatched Series", page.Markup);
    }

    private static LibraryEntryDto MakeEntry(string provider, string providerId, string title, LibraryMediaType mediaType = LibraryMediaType.Manga) =>
        new(provider, providerId, title, [], [], mediaType, null, "Synopsis", ["Fantasy"], 8.5, "Releasing", "10", null, DateTimeOffset.UtcNow, $"https://example.com/{providerId}");

    private sealed class FakeLibraryService : ILibraryService
    {
        public List<LibraryEntryDto> Trending { get; } = [];
        public List<LibraryEntryDto> SearchResults { get; } = [];
        public int TrendingCallCount { get; private set; }
        public int SearchCallCount { get; private set; }
        public string? LastSearchQuery { get; private set; }
        public LibraryMediaType? LastTrendingMediaType { get; private set; }

        public Task<LibrarySearchResponse> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            LastSearchQuery = query;
            return Task.FromResult(new LibrarySearchResponse { Items = [.. SearchResults] });
        }

        public Task<LibrarySearchResponse> GetTrendingAsync(LibraryMediaType? mediaType, int skip = 0, int take = 48, CancellationToken cancellationToken = default)
        {
            TrendingCallCount++;
            LastTrendingMediaType = mediaType;
            var page = Trending
                .Where(item => mediaType is null || item.MediaType == mediaType)
                .Skip(skip)
                .Take(take)
                .ToList();
            return Task.FromResult(new LibrarySearchResponse
            {
                Items = page,
                TotalCount = Trending.Count(item => mediaType is null || item.MediaType == mediaType),
                HasMore = skip + page.Count < Trending.Count(item => mediaType is null || item.MediaType == mediaType)
            });
        }

        public Task<LibraryCatalogSyncStatusDto> GetCatalogSyncStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LibraryCatalogSyncStatusDto());

        public Task TriggerCatalogResyncAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<LibraryEntryDto?> EnrichCatalogEntryAsync(string provider, string providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LibraryEntryDto?>(null);

        public Task<List<ProviderHealthDto>> GetProvidersHealthAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new List<ProviderHealthDto>());
        }

        public Task ToggleProviderAsync(string providerName, bool enabled, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public List<LibraryReadingProgressDto> ReadingProgress { get; } = [];
        public List<LibraryEntryDto> MyBookmarkedSeries { get; } = [];

        public Task<List<LibraryReadingProgressDto>> GetReadingProgressAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadingProgress);

        public Task<List<LibraryEntryDto>> GetMyBookmarkedSeriesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(MyBookmarkedSeries);
    }
}
