using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class AnimeCalendarControllerTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // WithWebHostBuilder spins up a distinct host, which re-runs ConfigureWebHost's
    // db.Database.EnsureDeleted() against the same SQLite file. Build the fake-provider
    // factory first and seed through its own Services so seeded data survives.
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateFactoryWithFakeAnilist(
        IntegrationTestWebApplicationFactory baseFactory, FakeAnilistScheduleProvider fake)
    {
        return baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IAnilistScheduleProvider));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddSingleton<IAnilistScheduleProvider>(fake);
            });
        });
    }

    [Fact]
    public async Task GetCandidates_ReturnsCandidatesFromProviderForBookmarkTitle()
    {
        var bookmarkId = Guid.NewGuid();
        var fake = new FakeAnilistScheduleProvider();
        fake.CandidatesByTitle["One Piece"] = [new AnimeMatchCandidateDto { AniListId = 21, RomajiTitle = "One Piece", Status = "RELEASING" }];

        var factory = CreateFactoryWithFakeAnilist(Factory, fake);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = bookmarkId, Title = "One Piece", Url = "https://example.com/op",
                Type = NodeType.Bookmark, Category = "Anime", SyncState = SyncState.Synced, Version = 1, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var result = await client.GetFromJsonAsync<List<AnimeMatchCandidateDto>>($"/api/anime-calendar/candidates/{bookmarkId}", Options);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal(21, result![0].AniListId);
    }

    [Fact]
    public async Task ConfirmMatch_SetsAniListIdAndMatchedTimestamp()
    {
        var bookmarkId = Guid.NewGuid();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = bookmarkId, Title = "One Piece", Type = NodeType.Bookmark, Category = "Anime",
                SyncState = SyncState.Synced, Version = 1, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/anime-calendar/match", new ConfirmAnimeMatchRequest { BookmarkId = bookmarkId, AniListId = 21 });
        response.EnsureSuccessStatusCode();

        using var scope2 = Factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var node = await db2.BookmarkNodes.FindAsync(bookmarkId);
        Assert.Equal(21, node!.AniListId);
        Assert.NotNull(node.AniListMatchedAt);
    }

    [Fact]
    public async Task ClearMatch_ResetsAniListFields()
    {
        var bookmarkId = Guid.NewGuid();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = bookmarkId, Title = "One Piece", Type = NodeType.Bookmark, Category = "Anime",
                AniListId = 21, AniListMatchedAt = DateTime.UtcNow,
                SyncState = SyncState.Synced, Version = 1, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        var response = await client.DeleteAsync($"/api/anime-calendar/match/{bookmarkId}");
        response.EnsureSuccessStatusCode();

        using var scope2 = Factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var node = await db2.BookmarkNodes.FindAsync(bookmarkId);
        Assert.Null(node!.AniListId);
        Assert.Null(node.AniListMatchedAt);
    }

    [Fact]
    public async Task GetSchedule_OnlyIncludesAnimeCategoryAndSplitsMatchedFromUnmatched()
    {
        var folderId = Guid.NewGuid();
        var subFolderId = Guid.NewGuid();
        var matchedId = Guid.NewGuid();
        var unmatchedId = Guid.NewGuid();
        var mangaId = Guid.NewGuid();

        var fake = new FakeAnilistScheduleProvider();
        fake.ScheduleByAniListId[21] = new AnimeScheduleResult("RELEASING", [new AnimeScheduleEpisode(1093, DateTimeOffset.UtcNow.AddDays(3))]);

        var factory = CreateFactoryWithFakeAnilist(Factory, fake);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.BookmarkNodes.AddRange(
                new BookmarkNode { Id = folderId, Title = "Anime", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now },
                new BookmarkNode { Id = subFolderId, ParentId = folderId, Title = "Sub", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now },
                new BookmarkNode { Id = matchedId, ParentId = subFolderId, Title = "One Piece", Url = "https://example.com/op", Type = NodeType.Bookmark, Category = "Anime", AniListId = 21, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now },
                new BookmarkNode { Id = unmatchedId, ParentId = folderId, Title = "Naruto", Type = NodeType.Bookmark, Category = "Anime", SyncState = SyncState.Synced, Version = 1, UpdatedAt = now },
                new BookmarkNode { Id = mangaId, ParentId = folderId, Title = "Some Manga", Type = NodeType.Bookmark, Category = "Manga", SyncState = SyncState.Synced, Version = 1, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var result = await client.GetFromJsonAsync<AnimeCalendarScheduleResponse>($"/api/anime-calendar/schedule?folderIds={folderId}", Options);

        Assert.NotNull(result);
        Assert.Single(result!.Entries);
        Assert.Equal(matchedId, result.Entries[0].BookmarkId);
        Assert.Equal(1093, result.Entries[0].EpisodeNumber);

        Assert.Single(result.UnmatchedBookmarks);
        Assert.Equal(unmatchedId, result.UnmatchedBookmarks[0].Id);
    }

    [Fact]
    public async Task GetSchedule_RelabelsEntry_WhenScheduleResolvesToSequelSeason()
    {
        // A bookmark matched to a finished season should surface its franchise's newer season:
        // schedule resolution follows the AniList SEQUEL chain and reports the new season via the
        // Resolved* fields, which the calendar uses to relabel the entry.
        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();
        var airing = DateTimeOffset.UtcNow.AddDays(3);

        var fakeAnilist = new FakeAnilistScheduleProvider();
        fakeAnilist.ScheduleByAniListId[166873] = new AnimeScheduleResult(
            "RELEASING",
            [new AnimeScheduleEpisode(3, airing)],
            ResolvedAniListId: 178789,
            ResolvedTitle: "Mushoku Tensei: Jobless Reincarnation Season 3",
            ResolvedCoverImageUrl: "https://example.com/s3.jpg");

        var factory = CreateFactoryWithFakeAnilist(Factory, fakeAnilist);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.BookmarkNodes.AddRange(
                new BookmarkNode { Id = folderId, Title = "Anime", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now },
                new BookmarkNode { Id = bookmarkId, ParentId = folderId, Title = "Mushoku Tensei S2 Part 2", Type = NodeType.Bookmark, Category = "Anime", AniListId = 166873, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var result = await client.GetFromJsonAsync<AnimeCalendarScheduleResponse>($"/api/anime-calendar/schedule?folderIds={folderId}", Options);

        Assert.NotNull(result);
        Assert.Single(result!.Entries);
        Assert.Equal("Mushoku Tensei: Jobless Reincarnation Season 3", result.Entries[0].Title);
        Assert.Equal(178789, result.Entries[0].AniListId);
        Assert.Equal(3, result.Entries[0].EpisodeNumber);
        Assert.Equal(1, result.AiringCount);
    }

    [Fact]
    public async Task GetSchedule_DetectsAnimeByTagsList_WhenCategoryFieldIsNotSet()
    {
        // Auto-tagging writes the "Anime" domain marker into Tags, not Category - Category is
        // only ever set by a manual metadata edit. This covers the path most real bookmarks take.
        var folderId = Guid.NewGuid();
        var taggedByTagsId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.BookmarkNodes.AddRange(
                new BookmarkNode { Id = folderId, Title = "Anime", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now },
                new BookmarkNode { Id = taggedByTagsId, ParentId = folderId, Title = "One Piece", Type = NodeType.Bookmark, Tags = "Anime,Action,Adventure", SyncState = SyncState.Synced, Version = 1, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        using var client = Factory.CreateClient();
        var result = await client.GetFromJsonAsync<AnimeCalendarScheduleResponse>($"/api/anime-calendar/schedule?folderIds={folderId}", Options);

        Assert.NotNull(result);
        Assert.Single(result!.UnmatchedBookmarks);
        Assert.Equal(taggedByTagsId, result.UnmatchedBookmarks[0].Id);
    }

    [Fact]
    public async Task GetSchedule_ReturnsEmpty_WhenNoFoldersSelected()
    {
        using var client = Factory.CreateClient();
        var result = await client.GetFromJsonAsync<AnimeCalendarScheduleResponse>("/api/anime-calendar/schedule", Options);

        Assert.NotNull(result);
        Assert.Empty(result!.Entries);
        Assert.Empty(result.UnmatchedBookmarks);
    }

    [Fact]
    public async Task AutoMatch_SetsAniListIdForConfidentMatchAndSkipsTheRest()
    {
        var folderId = Guid.NewGuid();
        var confidentId = Guid.NewGuid();
        var ambiguousId = Guid.NewGuid();

        var fake = new FakeAnilistScheduleProvider();
        fake.BestMatchByTitle["One Piece"] = new AnimeMatchCandidateDto { AniListId = 21, RomajiTitle = "One Piece", Status = "RELEASING" };
        fake.BestMatchByTitle["Some Ambiguous Title"] = null;

        var factory = CreateFactoryWithFakeAnilist(Factory, fake);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.BookmarkNodes.AddRange(
                new BookmarkNode { Id = folderId, Title = "Anime", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now },
                new BookmarkNode { Id = confidentId, ParentId = folderId, Title = "One Piece", Type = NodeType.Bookmark, Category = "Anime", SyncState = SyncState.Synced, Version = 1, UpdatedAt = now },
                new BookmarkNode { Id = ambiguousId, ParentId = folderId, Title = "Some Ambiguous Title", Type = NodeType.Bookmark, Category = "Anime", SyncState = SyncState.Synced, Version = 1, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/anime-calendar/auto-match", new AutoMatchAnimeRequest { FolderIds = [folderId] });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AutoMatchAnimeResponse>(Options);

        Assert.NotNull(result);
        Assert.Single(result!.Matched);
        Assert.Equal(confidentId, result.Matched[0].BookmarkId);
        Assert.Single(result.Skipped);
        Assert.Equal(ambiguousId, result.Skipped[0].BookmarkId);

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var confident = await db2.BookmarkNodes.FindAsync(confidentId);
        Assert.Equal(21, confident!.AniListId);
    }

    [Fact]
    public async Task AutoMatch_ReportsUnavailable_AndMatchesNothing_WhenAniListIsDown()
    {
        // AniList is the only anime source. A global outage should degrade gracefully: nothing is
        // matched, the response flags the outage, and the bookmark is left unmatched to retry later.
        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();

        var fakeAnilist = new FakeAnilistScheduleProvider { ThrowUnavailable = true };

        var factory = CreateFactoryWithFakeAnilist(Factory, fakeAnilist);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.BookmarkNodes.AddRange(
                new BookmarkNode { Id = folderId, Title = "Anime", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now },
                new BookmarkNode { Id = bookmarkId, ParentId = folderId, Title = "One Piece", Type = NodeType.Bookmark, Category = "Anime", SyncState = SyncState.Synced, Version = 1, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/anime-calendar/auto-match", new AutoMatchAnimeRequest { FolderIds = [folderId] });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AutoMatchAnimeResponse>(Options);

        Assert.NotNull(result);
        Assert.True(result!.AniListUnavailable);
        Assert.Empty(result.Matched);
        // Outage detected mid-run: the bookmark is left untouched (not even added to Skipped) so it
        // retries next time, rather than burning its match-attempt cooldown on a transient failure.
        Assert.Empty(result.Skipped);

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var node = await db2.BookmarkNodes.FindAsync(bookmarkId);
        Assert.Null(node!.AniListId);
    }

    [Fact]
    public async Task AutoMatch_SkipsBookmarksAttemptedRecently_AndReportsCooldownCount()
    {
        var folderId = Guid.NewGuid();
        var recentlyAttemptedId = Guid.NewGuid();

        var fake = new FakeAnilistScheduleProvider();
        fake.BestMatchByTitle["Some Ambiguous Title"] = null;
        var factory = CreateFactoryWithFakeAnilist(Factory, fake);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.BookmarkNodes.AddRange(
                new BookmarkNode { Id = folderId, Title = "Anime", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now },
                new BookmarkNode
                {
                    Id = recentlyAttemptedId, ParentId = folderId, Title = "Some Ambiguous Title", Type = NodeType.Bookmark, Category = "Anime",
                    LastMatchAttemptAt = now.AddHours(-1), // well inside the 7-day cooldown
                    SyncState = SyncState.Synced, Version = 1, UpdatedAt = now
                });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/anime-calendar/auto-match", new AutoMatchAnimeRequest { FolderIds = [folderId] });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AutoMatchAnimeResponse>(Options);

        Assert.NotNull(result);
        Assert.Equal(1, result!.SkippedCooldownCount);
        Assert.Empty(result.Matched);
        Assert.Empty(result.Skipped); // not re-attempted at all, so not even in the "tried, no match" bucket
    }

    [Fact]
    public async Task GetSchedule_QueriesFinishedMatch_ToDiscoverNewerSeason()
    {
        // Finished-status matches are no longer skipped - they are queried so schedule resolution
        // can follow a SEQUEL chain to a newer airing season. The fake returns an upcoming episode,
        // so the provider is hit once and the series counts as airing.
        var folderId = Guid.NewGuid();
        var finishedId = Guid.NewGuid();

        var fake = new FakeAnilistScheduleProvider();
        fake.ScheduleByAniListId[21] = new AnimeScheduleResult("RELEASING", [new AnimeScheduleEpisode(1000, DateTimeOffset.UtcNow.AddDays(3))]);
        var factory = CreateFactoryWithFakeAnilist(Factory, fake);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.BookmarkNodes.AddRange(
                new BookmarkNode { Id = folderId, Title = "Anime", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now },
                new BookmarkNode
                {
                    Id = finishedId, ParentId = folderId, Title = "One Piece", Type = NodeType.Bookmark, Category = "Anime",
                    AniListId = 21, MediaStatus = "FINISHED",
                    SyncState = SyncState.Synced, Version = 1, UpdatedAt = now
                });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var result = await client.GetFromJsonAsync<AnimeCalendarScheduleResponse>($"/api/anime-calendar/schedule?folderIds={folderId}", Options);

        Assert.NotNull(result);
        Assert.Single(result!.Entries);
        Assert.Equal(1, result.AiringCount);
        Assert.Equal(0, result.FinishedCount);
        Assert.Equal(1, fake.GetAiringScheduleCallCount);
    }

    [Fact]
    public async Task GetSchedule_SkipsAniListCall_ForSeriesRecentlyConfirmedNoUpcoming()
    {
        // Persistent no-airing cache: a series whose ScheduleCheckedAt was stamped recently (AniList
        // already confirmed it has no upcoming episodes) must not be re-queried on the next load -
        // this is what keeps 100+ finished series off AniList's rate limit.
        var folderId = Guid.NewGuid();
        var finishedId = Guid.NewGuid();

        var fake = new FakeAnilistScheduleProvider();
        // If the series were (wrongly) queried, the fake would return an episode - so an empty
        // result proves the call was skipped.
        fake.ScheduleByAniListId[21] = new AnimeScheduleResult("RELEASING", [new AnimeScheduleEpisode(1000, DateTimeOffset.UtcNow.AddDays(3))]);
        var factory = CreateFactoryWithFakeAnilist(Factory, fake);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            db.BookmarkNodes.AddRange(
                new BookmarkNode { Id = folderId, Title = "Anime", Type = NodeType.Folder, SyncState = SyncState.Synced, Version = 1, UpdatedAt = now },
                new BookmarkNode
                {
                    Id = finishedId, ParentId = folderId, Title = "One Piece", Type = NodeType.Bookmark, Category = "Anime",
                    AniListId = 21, MediaStatus = "FINISHED", ScheduleCheckedAt = now.AddHours(-1),
                    SyncState = SyncState.Synced, Version = 1, UpdatedAt = now
                });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var result = await client.GetFromJsonAsync<AnimeCalendarScheduleResponse>($"/api/anime-calendar/schedule?folderIds={folderId}", Options);

        Assert.NotNull(result);
        Assert.Empty(result!.Entries);
        Assert.Equal(0, result.AiringCount);
        Assert.Equal(1, result.FinishedCount);
        Assert.Equal(0, fake.GetAiringScheduleCallCount);
    }

    private sealed class FakeAnilistScheduleProvider : IAnilistScheduleProvider
    {
        public Dictionary<string, List<AnimeMatchCandidateDto>> CandidatesByTitle { get; } = new();
        public Dictionary<int, AnimeScheduleResult> ScheduleByAniListId { get; } = new();
        public Dictionary<string, AnimeMatchCandidateDto?> BestMatchByTitle { get; } = new();
        public bool ThrowUnavailable { get; set; }

        public Task<List<AnimeMatchCandidateDto>> SearchCandidatesAsync(string title, string? url, CancellationToken cancellationToken)
            => Task.FromResult(CandidatesByTitle.TryGetValue(title, out var c) ? c : []);

        public int GetAiringScheduleCallCount { get; private set; }

        public Task<AnimeScheduleResult> GetAiringScheduleAsync(int aniListId, CancellationToken cancellationToken)
        {
            GetAiringScheduleCallCount++;
            return Task.FromResult(ScheduleByAniListId.TryGetValue(aniListId, out var s) ? s : new AnimeScheduleResult(null, []));
        }

        public Task<Dictionary<int, AnimeScheduleResult>> GetAiringSchedulesBatchAsync(IReadOnlyList<int> aniListIds, CancellationToken cancellationToken)
        {
            var result = new Dictionary<int, AnimeScheduleResult>();
            foreach (var id in aniListIds)
            {
                GetAiringScheduleCallCount++;
                result[id] = ScheduleByAniListId.TryGetValue(id, out var s) ? s : new AnimeScheduleResult(null, []);
            }
            return Task.FromResult(result);
        }

        public Task<Dictionary<Guid, BestMatchLookupResult>> FindBestMatchesBatchAsync(
            IReadOnlyList<(Guid Id, string Title, string? Url)> items, CancellationToken cancellationToken)
        {
            var results = new Dictionary<Guid, BestMatchLookupResult>();
            foreach (var item in items)
            {
                results[item.Id] = ThrowUnavailable
                    ? new BestMatchLookupResult(null, Unavailable: true)
                    : new BestMatchLookupResult(BestMatchByTitle.TryGetValue(item.Title, out var best) ? best : null, Unavailable: false);
            }
            return Task.FromResult(results);
        }

        public bool IsAniListDegraded { get; set; }
    }

}
