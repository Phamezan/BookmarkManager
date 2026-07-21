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
        var service = new BookmarkTaggingService(anilist, mangaUpdates, new FakeKitsuProvider([]), new FakeCatalogProvider([]), new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);

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
        var service = new BookmarkTaggingService(anilist, mangaUpdates, new FakeKitsuProvider([]), new FakeCatalogProvider([]), new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);

        var tags = await service.GetTagsAsync("One Piece - Episode 1092", "https://crunchyroll.com/watch/x", "Anime", BookmarkTagDomainDto.Auto, CancellationToken.None);

        Assert.Equal(1, anilist.CallCount);
        Assert.Equal(0, mangaUpdates.CallCount);
        Assert.Equal(BookmarkTagDomain.Anime, anilist.LastDomain);
        Assert.Equal(["Anime", "Shounen"], tags);
    }

    [Fact]
    public async Task GetTagsAsync_MangaBookmarkCallsMangaUpdatesOnly()
    {
        var anilist = new FakeAnilistProvider(["Anime"]);
        var mangaUpdates = new FakeMangaUpdatesProvider(["Action"]);
        var service = new BookmarkTaggingService(anilist, mangaUpdates, new FakeKitsuProvider([]), new FakeCatalogProvider([]), new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);

        var tags = await service.GetTagsAsync("Solo Leveling - Chapter 1", "https://mangadex.org/title/x", "Manhwa", BookmarkTagDomainDto.Auto, CancellationToken.None);

        Assert.Equal(0, anilist.CallCount);
        Assert.Equal(1, mangaUpdates.CallCount);
        Assert.Equal(BookmarkTagDomain.Manga, mangaUpdates.LastDomain);
        Assert.Equal(["Manga", "Action"], tags);
    }

    [Fact]
    public async Task GetTagsForBatchAsync_DeduplicatesSameProviderAndCleanTitle()
    {
        var anilist = new FakeAnilistProvider(["Anime"]);
        var mangaUpdates = new FakeMangaUpdatesProvider(["Action"]);
        var service = new BookmarkTaggingService(anilist, mangaUpdates, new FakeKitsuProvider([]), new FakeCatalogProvider([]), new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);
        var request = new[]
        {
            new BookmarkTagCandidateDto { Id = Guid.NewGuid(), Title = "Solo Leveling - Chapter 1", Url = "https://mangadex.org/title/x" },
            new BookmarkTagCandidateDto { Id = Guid.NewGuid(), Title = "Solo Leveling - Chapter 2", Url = "https://mangadex.org/title/x" }
        };

        var result = await service.GetTagsForBatchAsync(request, "Manhwa", BookmarkTagDomainDto.Auto, CancellationToken.None);

        Assert.Equal(0, anilist.CallCount);
        Assert.Equal(1, mangaUpdates.CallCount);
        Assert.Equal(2, result.Tags.Count);
    }

    [Fact]
    public async Task GetTagsAsync_AniListRejection_SkipsFallbackReturnsDomainTag()
    {
        var anilist = new FakeAnilistProvider([], wasRejected: true, rejectionReason: "Similarity threshold not met");
        var mangaUpdates = new FakeMangaUpdatesProvider(["Manga"]);
        var service = new BookmarkTaggingService(anilist, mangaUpdates, new FakeKitsuProvider([]), new FakeCatalogProvider([]), new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);

        var tags = await service.GetTagsAsync("One Piece - Episode 1092", "https://crunchyroll.com/watch/x", "Anime", BookmarkTagDomainDto.Auto, CancellationToken.None);

        Assert.Equal(1, anilist.CallCount);
        Assert.Equal(["Anime"], tags);
    }

    [Fact]
    public async Task GetTagsAsync_MangaUpdatesRejection_SkipsFallbackReturnsDomainTag()
    {
        var anilist = new FakeAnilistProvider(["Anime"]);
        var mangaUpdates = new FakeMangaUpdatesProvider([], wasRejected: true, rejectionReason: "Domain mismatch");
        var service = new BookmarkTaggingService(anilist, mangaUpdates, new FakeKitsuProvider([]), new FakeCatalogProvider([]), new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);

        var tags = await service.GetTagsAsync("Solo Leveling - Chapter 1", "https://mangadex.org/title/x", "Manhwa", BookmarkTagDomainDto.Auto, CancellationToken.None);

        Assert.Equal(1, mangaUpdates.CallCount);
        Assert.Equal(["Manga"], tags);
    }

    [Fact]
    public async Task GetTagsAsync_GeneralDomainWithWeakSignal_QueriesProvidersInParallel()
    {
        var anilist = new FakeAnilistProvider([]);
        var mangaUpdates = new FakeMangaUpdatesProvider(["Action", "Shounen"]);
        var kitsu = new FakeKitsuProvider([]);
        var catalog = new FakeCatalogProvider([]);
        var service = new BookmarkTaggingService(anilist, mangaUpdates, kitsu, catalog, new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);

        var tags = await service.GetTagsAsync("Solo Leveling", "https://unrecognized-site.com/series/solo-leveling", "General", BookmarkTagDomainDto.Auto, CancellationToken.None);

        Assert.Equal(1, anilist.CallCount);
        Assert.Equal(2, mangaUpdates.CallCount); 
        Assert.Equal(["Action", "Shounen"], tags);
    }

    [Fact]
    public async Task GetTagsAsync_MultipleProvidersMatch_CombinesAndDeduplicatesTags()
    {
        var anilist = new FakeAnilistProvider(["Shounen", "Magic"]);
        var mangaUpdates = new FakeMangaUpdatesProvider(["Action", "Magic"]);
        var kitsu = new FakeKitsuProvider(["Novel", "Fantasy"]);
        var catalog = new FakeCatalogProvider(["Novel", "System"]);
        var service = new BookmarkTaggingService(anilist, mangaUpdates, kitsu, catalog, new TagExtractorService(), NullLogger<BookmarkTaggingService>.Instance);

        // When domain is Novel, we query MangaUpdates (Novel) + Kitsu (Novel) + Catalog (Novel)
        var tags = await service.GetTagsAsync("God of Fishing", null, "Novel", BookmarkTagDomainDto.Auto, CancellationToken.None);

        Assert.Equal(0, anilist.CallCount);
        Assert.Equal(1, mangaUpdates.CallCount);
        Assert.Equal(1, kitsu.CallCount);
        Assert.Equal(1, catalog.CallCount);

        // Deduplicated: "Novel", "Fantasy", "System", "Action", "Magic" (with "Novel" placed at index 0)
        Assert.Equal(new[] { "Novel", "Fantasy", "System", "Action", "Magic" }, tags);
    }

    [Fact]
    public async Task GetTagsForBatchAsync_RejectsMisleadingCanonicalTitleFromEarlierProvider()
    {
        var mangaUpdates = new FakeMangaUpdatesProvider(["Novel", "Space", "Sci-Fi"], canonicalTitle: "Galaxy");
        var catalog = new FakeCatalogProvider(["Novel", "Space", "VR", "Sci-Fi"], canonicalTitle: "Transcendence Due To A System Error");
        var service = new BookmarkTaggingService(
            new FakeAnilistProvider([]),
            mangaUpdates,
            new FakeKitsuProvider([]),
            catalog,
            new TagExtractorService(),
            NullLogger<BookmarkTaggingService>.Instance);

        var request = new[]
        {
            new BookmarkTagCandidateDto
            {
                Id = Guid.NewGuid(),
                Title = "Transcendence Due To A System Error - Chapter 110 - Galaxy Translations",
                Url = "https://example.com/x"
            }
        };

        var result = await service.GetTagsForBatchAsync(request, "Novel", BookmarkTagDomainDto.Auto, CancellationToken.None);

        Assert.Equal("Transcendence Due To A System Error — Chapter 110", result.SuggestedTitles[request[0].Id]);
    }

    private sealed class FakeAnilistProvider(List<string> tags, bool wasRejected = false, string? rejectionReason = null, string? canonicalTitle = null) : IAnilistTagProvider
    {
        public int CallCount { get; private set; }
        public BookmarkTagDomain? LastDomain { get; private set; }

        public Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            LastDomain = context.Domain;
            return Task.FromResult(new ProviderTagResult(tags, wasRejected, rejectionReason, canonicalTitle));
        }
    }

    private sealed class FakeMangaUpdatesProvider(List<string> tags, bool wasRejected = false, string? rejectionReason = null, string? canonicalTitle = null) : IMangaUpdatesTagProvider
    {
        public int CallCount { get; private set; }
        public BookmarkTagDomain? LastDomain { get; private set; }

        public Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            LastDomain = context.Domain;
            return Task.FromResult(new ProviderTagResult(tags, wasRejected, rejectionReason, canonicalTitle));
        }
    }

    private sealed class FakeKitsuProvider(List<string> tags, bool wasRejected = false, string? rejectionReason = null, string? canonicalTitle = null) : IKitsuTagProvider
    {
        public int CallCount { get; private set; }
        public BookmarkTagDomain? LastDomain { get; private set; }

        public Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            LastDomain = context.Domain;
            return Task.FromResult(new ProviderTagResult(tags, wasRejected, rejectionReason, canonicalTitle));
        }
    }

    private sealed class FakeCatalogProvider(List<string> tags, bool wasRejected = false, string? rejectionReason = null, string? canonicalTitle = null) : ICatalogTagProvider
    {
        public int CallCount { get; private set; }
        public BookmarkTagDomain? LastDomain { get; private set; }

        public Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            LastDomain = context.Domain;
            return Task.FromResult(new ProviderTagResult(tags, wasRejected, rejectionReason, canonicalTitle));
        }
    }
}
