using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Client.Pages;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using BookmarkManager.Client.ComponentTests.TestDoubles;
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

        context.Services.AddSingleton<UndoService>();
        var fakeBookmark = new FakeBookmarkService();
        fakeBookmark.FolderTree.Add(new FolderTreeNodeDto { Id = Guid.NewGuid(), Title = "Default Folder" });
        context.Services.AddSingleton<IBookmarkService>(fakeBookmark);

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

        context.Services.AddSingleton<UndoService>();
        var fakeBookmark = new FakeBookmarkService();
        fakeBookmark.FolderTree.Add(new FolderTreeNodeDto { Id = Guid.NewGuid(), Title = "Default Folder" });
        context.Services.AddSingleton<IBookmarkService>(fakeBookmark);

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
    public async Task Library_TrackButton_MarksItemTracked()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        context.Services.AddSingleton<UndoService>();
        var fakeBookmark = new FakeBookmarkService();
        fakeBookmark.FolderTree.Add(new FolderTreeNodeDto { Id = Guid.NewGuid(), Title = "Default Folder" });
        context.Services.AddSingleton<IBookmarkService>(fakeBookmark);

        var fake = new FakeLibraryService();
        fake.Trending.Add(MakeEntry("AniList", "1", "Frieren"));
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".lib-track-btn")));

        page.Find(".lib-track-btn").Click();

        page.WaitForAssertion(() => Assert.Contains("Track Series", page.Markup));

        var trackButton = page.FindAll("button").First(b => b.TextContent.Contains("Track series"));
        trackButton.Click();

        page.WaitForAssertion(() => Assert.Contains("Tracked", page.Markup));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".lib-progress-btn")));
    }

    [Fact]
    public async Task Library_ProgressUpdate_RefreshesRenderedChapter()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<UndoService>();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var bookmarkId = Guid.NewGuid();
        var fake = new FakeLibraryService();
        fake.Trending.Add(MakeEntry("AniList", "1", "Frieren"));
        fake.Tracked.Add(new TrackedSeriesDto
        {
            BookmarkId = bookmarkId,
            Provider = "AniList",
            ProviderId = "1",
            ChaptersRead = 2,
            LatestKnownChapter = "10"
        });
        context.Services.AddSingleton<ILibraryService>(fake);

        var page = RenderPage(context);
        page.WaitForAssertion(() => Assert.Contains("Ch. 2", page.Markup));

        page.FindAll(".lib-progress-btn").First(button => button.TextContent.Contains("+1")).Click();

        page.WaitForAssertion(() => Assert.Contains("Ch. 3", page.Markup));
    }

    [Fact]
    public async Task Library_LoadMore_AppendsNextPageAndHidesButtonWhenExhausted()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        context.Services.AddSingleton<UndoService>();
        var fakeBookmark = new FakeBookmarkService();
        fakeBookmark.FolderTree.Add(new FolderTreeNodeDto { Id = Guid.NewGuid(), Title = "Default Folder" });
        context.Services.AddSingleton<IBookmarkService>(fakeBookmark);

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

    private static LibraryEntryDto MakeEntry(string provider, string providerId, string title) =>
        new(provider, providerId, title, [], [], LibraryMediaType.Manga, null, "Synopsis", ["Fantasy"], 8.5, "Releasing", "10", null, DateTimeOffset.UtcNow, $"https://example.com/{providerId}");

    private sealed class FakeLibraryService : ILibraryService
    {
        public List<LibraryEntryDto> Trending { get; } = [];
        public List<LibraryEntryDto> SearchResults { get; } = [];
        public List<TrackedSeriesDto> Tracked { get; } = [];
        public int TrendingCallCount { get; private set; }
        public int SearchCallCount { get; private set; }
        public string? LastSearchQuery { get; private set; }

        public Task<LibrarySearchResponse> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            LastSearchQuery = query;
            return Task.FromResult(new LibrarySearchResponse { Items = [.. SearchResults] });
        }

        public Task<LibrarySearchResponse> GetTrendingAsync(LibraryMediaType? mediaType, int skip = 0, int take = 48, CancellationToken cancellationToken = default)
        {
            TrendingCallCount++;
            var page = Trending.Skip(skip).Take(take).ToList();
            return Task.FromResult(new LibrarySearchResponse
            {
                Items = page,
                TotalCount = Trending.Count,
                HasMore = skip + page.Count < Trending.Count
            });
        }

        public Task<LibraryCatalogSyncStatusDto> GetCatalogSyncStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new LibraryCatalogSyncStatusDto());

        public Task TriggerCatalogResyncAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<List<TrackedSeriesDto>> GetTrackedSeriesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Tracked.Select(item => new TrackedSeriesDto
            {
                Id = item.Id,
                BookmarkId = item.BookmarkId,
                Provider = item.Provider,
                ProviderId = item.ProviderId,
                ChaptersRead = item.ChaptersRead,
                LatestKnownChapter = item.LatestKnownChapter,
                LatestChapterUrl = item.LatestChapterUrl
            }).ToList());
        }

        public Task<BookmarkNodeDto> TrackSeriesAsync(TrackLibraryEntryRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new BookmarkNodeDto { Id = Guid.NewGuid(), Title = request.Title, Url = request.SourceUrl });
        }

        public Task<ReleaseWatcherStatusDto> GetWatcherStatusAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ReleaseWatcherStatusDto());
        }

        public Task<ReleaseWatcherSettingsDto> GetWatcherSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReleaseWatcherSettingsDto());

        public Task<ReleaseWatcherSettingsDto> UpdateWatcherSettingsAsync(
            ReleaseWatcherSettingsDto settings,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task TriggerWatcherAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<TrackedSeriesDto> CheckSeriesReleaseAsync(Guid bookmarkId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TrackedSeriesDto { BookmarkId = bookmarkId });
        }

        public Task<TrackedSeriesDto> UpdateProgressAsync(Guid bookmarkId, double value, CancellationToken cancellationToken = default)
        {
            var tracked = Tracked.Single(item => item.BookmarkId == bookmarkId);
            tracked.ChaptersRead = value;
            return Task.FromResult(tracked);
        }

        public Task<List<ProviderHealthDto>> GetProvidersHealthAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new List<ProviderHealthDto>());
        }

        public Task ToggleProviderAsync(string providerName, bool enabled, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
