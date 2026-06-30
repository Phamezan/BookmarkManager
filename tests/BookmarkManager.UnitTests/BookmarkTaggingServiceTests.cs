using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests;

public sealed class BookmarkTaggingServiceTests
{
    [Fact]
    public async Task GetTagsAsync_GeneralBookmarkDoesNotCallExternalProviders()
    {
        var anilist = new FakeAnilistProvider(["Anime"]);
        var mangaUpdates = new FakeMangaUpdatesProvider(["Manga"]);
        var service = new BookmarkTaggingService(anilist, mangaUpdates, new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);

        var tags = await service.GetTagsAsync("dotnet aspnetcore", "https://github.com/dotnet/aspnetcore", "Development", BookmarkTagDomainDto.Auto, CancellationToken.None);

        Assert.Equal(0, anilist.CallCount);
        Assert.Equal(0, mangaUpdates.CallCount);
        Assert.Contains("Development", tags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTagsAsync_AnimeBookmarkCallsAniListOnly()
    {
        var anilist = new FakeAnilistProvider(["Shounen"]);
        var mangaUpdates = new FakeMangaUpdatesProvider(["Manga"]);
        var service = new BookmarkTaggingService(anilist, mangaUpdates, new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);

        var tags = await service.GetTagsAsync("One Piece - Episode 1092", "https://crunchyroll.com/watch/x", "Anime", BookmarkTagDomainDto.Auto, CancellationToken.None);

        Assert.Equal(1, anilist.CallCount);
        Assert.Equal(0, mangaUpdates.CallCount);
        Assert.Equal(BookmarkTagDomain.Anime, anilist.LastDomain);
        Assert.Equal(["Shounen"], tags);
    }

    [Fact]
    public async Task GetTagsAsync_MangaBookmarkCallsMangaUpdatesOnly()
    {
        var anilist = new FakeAnilistProvider(["Anime"]);
        var mangaUpdates = new FakeMangaUpdatesProvider(["Action"]);
        var service = new BookmarkTaggingService(anilist, mangaUpdates, new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);

        var tags = await service.GetTagsAsync("Solo Leveling - Chapter 1", "https://mangadex.org/title/x", "Manhwa", BookmarkTagDomainDto.Auto, CancellationToken.None);

        Assert.Equal(0, anilist.CallCount);
        Assert.Equal(1, mangaUpdates.CallCount);
        Assert.Equal(BookmarkTagDomain.Manga, mangaUpdates.LastDomain);
        Assert.Equal(["Action"], tags);
    }

    [Fact]
    public async Task GetTagsForBatchAsync_DeduplicatesSameProviderAndCleanTitle()
    {
        var anilist = new FakeAnilistProvider(["Anime"]);
        var mangaUpdates = new FakeMangaUpdatesProvider(["Action"]);
        var service = new BookmarkTaggingService(anilist, mangaUpdates, new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);
        var request = new[]
        {
            new BookmarkTagCandidateDto { Id = Guid.NewGuid(), Title = "Solo Leveling - Chapter 1", Url = "https://mangadex.org/title/x" },
            new BookmarkTagCandidateDto { Id = Guid.NewGuid(), Title = "Solo Leveling - Chapter 2", Url = "https://mangadex.org/title/x" }
        };

        var result = await service.GetTagsForBatchAsync(request, "Manhwa", BookmarkTagDomainDto.Auto, CancellationToken.None);

        Assert.Equal(0, anilist.CallCount);
        Assert.Equal(1, mangaUpdates.CallCount);
        Assert.Equal(2, result.Count);
    }

    private sealed class FakeAnilistProvider(List<string> tags) : IAnilistTagProvider
    {
        public int CallCount { get; private set; }
        public BookmarkTagDomain? LastDomain { get; private set; }

        public Task<List<string>> GetTagsForTitleAsync(string title, string? url, BookmarkTagDomain domain, CancellationToken cancellationToken)
        {
            CallCount++;
            LastDomain = domain;
            return Task.FromResult(tags);
        }
    }

    private sealed class FakeMangaUpdatesProvider(List<string> tags) : IMangaUpdatesTagProvider
    {
        public int CallCount { get; private set; }
        public BookmarkTagDomain? LastDomain { get; private set; }

        public Task<List<string>> GetTagsForTitleAsync(string title, string? url, BookmarkTagDomain domain, CancellationToken cancellationToken)
        {
            CallCount++;
            LastDomain = domain;
            return Task.FromResult(tags);
        }
    }
}
