using System.Net;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests;

public sealed class AiBookmarkAutoTaggingServiceTests
{
    [Fact]
    public async Task TagFolderAsync_LoadsDescendantFoldersAndTagsTheirBookmarks()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();
        await fixture.SeedAsync(
            Folder(rootId, null, "Media"),
            Folder(childId, rootId, "Anime"),
            Bookmark(bookmarkId, childId, "One Piece Episode 1092", "https://crunchyroll.com/watch/one-piece"));
        fixture.Anilist.SetTags("One Piece", ["Shounen"]);

        var summary = await fixture.Service.TagFolderAsync(rootId, forceRefresh: false, CancellationToken.None);

        var bookmark = await fixture.Db.BookmarkNodes.SingleAsync(node => node.Id == bookmarkId);
        Assert.Equal("Anime,Shounen", bookmark.Tags);
        Assert.Equal(1, summary.TotalCandidates);
        Assert.Equal(1, summary.Tagged);
        Assert.Equal(1, fixture.Anilist.CallCount);
        Assert.Equal(0, fixture.Identifier.RequestCount); // Bypasses AI due to deterministic Anime classifier
    }

    [Fact]
    public async Task TagFolderAsync_SkipsAlreadyTaggedBookmarksByDefault()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();
        await fixture.SeedAsync(
            Folder(folderId, null, "Manga"),
            Bookmark(bookmarkId, folderId, "Solo Leveling Chapter 1", "https://asuracomic.net/series/solo-leveling", tags: "Existing"));

        var summary = await fixture.Service.TagFolderAsync(folderId, forceRefresh: false, CancellationToken.None);

        var bookmark = await fixture.Db.BookmarkNodes.SingleAsync(node => node.Id == bookmarkId);
        Assert.Equal("Existing", bookmark.Tags);
        Assert.Equal(1, summary.TotalCandidates);
        Assert.Equal(0, summary.Tagged);
        Assert.Equal(1, summary.SkippedAlreadyTagged);
        Assert.Equal(0, fixture.MangaUpdates.CallCount);
        Assert.Equal(0, fixture.Identifier.RequestCount);
    }

    [Fact]
    public async Task TagFolderAsync_ForceRefreshOverwritesExistingTags()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();
        await fixture.SeedAsync(
            Folder(folderId, null, "Manhwa"),
            Bookmark(bookmarkId, folderId, "Solo Leveling Chapter 1", "https://asuracomic.net/series/solo-leveling", tags: "Existing,Old"));
        fixture.MangaUpdates.SetTags("Solo Leveling", ["Action", "Fantasy"]);

        var summary = await fixture.Service.TagFolderAsync(folderId, forceRefresh: true, CancellationToken.None);

        var bookmark = await fixture.Db.BookmarkNodes.SingleAsync(node => node.Id == bookmarkId);
        Assert.Equal("Manhwa,Action,Fantasy", bookmark.Tags);
        Assert.Equal(1, summary.Tagged);
        Assert.Equal(0, summary.SkippedAlreadyTagged);
    }

    [Fact]
    public async Task TagFolderAsync_SkipsLowConfidenceIdentification()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();
        await fixture.SeedAsync(
            Folder(folderId, null, "General Folder"),
            Bookmark(bookmarkId, folderId, "Ambiguous Chapter 1", "https://example.com/ambiguous"));
        fixture.Identifier.EnqueueIdentification(bookmarkId, "Ambiguous", 0.69, AiSeriesSourceHint.Novel);

        var summary = await fixture.Service.TagFolderAsync(folderId, forceRefresh: false, CancellationToken.None);

        var bookmark = await fixture.Db.BookmarkNodes.SingleAsync(node => node.Id == bookmarkId);
        Assert.Null(bookmark.Tags);
        Assert.Equal(1, summary.TotalCandidates);
        Assert.Equal(1, summary.SkippedLowConfidence);
        Assert.Equal(0, fixture.NovelFull.CallCount);
    }

    [Fact]
    public async Task TagFolderAsync_DuplicateChaptersOfSameCanonicalTitleFetchSourceTagsOnce()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await fixture.SeedAsync(
            Folder(folderId, null, "Light Novels"),
            Bookmark(firstId, folderId, "A Monster Who Levels Up Chapter 48", "https://lightnovels.me/a-monster-who-levels-up-48"),
            Bookmark(secondId, folderId, "A Monster Who Levels Up Chapter 49", "https://lightnovels.me/a-monster-who-levels-up-49"));
        fixture.NovelFull.SetTags("A Monster Who Levels Up", ["Fantasy", "Level System"]);

        var summary = await fixture.Service.TagFolderAsync(folderId, forceRefresh: false, CancellationToken.None);

        var bookmarks = await fixture.Db.BookmarkNodes.Where(node => node.ParentId == folderId).OrderBy(node => node.Title).ToListAsync();
        Assert.All(bookmarks, bookmark => Assert.Equal("Novel,Fantasy,Level System", bookmark.Tags));
        Assert.Equal(2, summary.Tagged);
        Assert.Equal(1, fixture.NovelFull.CallCount);
    }

    [Fact]
    public async Task TagFolderAsync_FlushesTagsIncrementallyEveryTenBookmarks()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var nodes = new List<BookmarkNode> { Folder(folderId, null, "Light Novels") };
        for (var i = 0; i < 12; i++)
        {
            var id = Guid.NewGuid();
            var series = $"Unique Novel {i:D2}";
            var title = $"{series} Chapter 1";
            nodes.Add(Bookmark(id, folderId, title, $"https://lightnovels.me/unique-novel-{i}", position: i));
            fixture.NovelFull.SetTags(series, ["Fantasy"]);
        }

        await fixture.SeedAsync(nodes.ToArray());

        var summary = await fixture.Service.TagFolderAsync(folderId, forceRefresh: false, CancellationToken.None);

        Assert.Equal(12, summary.Tagged);
        Assert.Equal(1, summary.Messages.Count(message => message.StartsWith("Saved 10 tagged bookmark(s)", StringComparison.Ordinal)));
        Assert.Equal(1, summary.Messages.Count(message => message.StartsWith("Saved 2 tagged bookmark(s)", StringComparison.Ordinal)));

        var taggedCount = await fixture.Db.BookmarkNodes.CountAsync(node => node.ParentId == folderId && node.Tags != null);
        Assert.Equal(12, taggedCount);
    }

    [Fact]
    public async Task TagFolderAsync_WithMaxCandidates_ReportsHasMoreWhenMoreRemain()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var nodes = new List<BookmarkNode> { Folder(folderId, null, "Light Novels") };
        for (var i = 0; i < 25; i++)
        {
            var id = Guid.NewGuid();
            var series = $"Batch Novel {i:D2}";
            nodes.Add(Bookmark(id, folderId, $"{series} Chapter 1", $"https://lightnovels.me/batch-novel-{i}", position: i));
            fixture.NovelFull.SetTags(series, ["Fantasy"]);
        }

        await fixture.SeedAsync(nodes.ToArray());

        var summary = await fixture.Service.TagFolderAsync(
            folderId,
            forceRefresh: false,
            maxCandidates: 10,
            excludedBookmarkIds: [],
            CancellationToken.None);

        Assert.Equal(10, summary.ProcessedBookmarkIds.Count);
        Assert.Equal(10, summary.Tagged);
        Assert.True(summary.HasMore);
        Assert.Equal(15, summary.RemainingCandidates);
    }

    [Fact]
    public async Task TagFolderAsync_WithMaxCandidates_ReportsNoMoreWhenBatchExhaustsRemainder()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var nodes = new List<BookmarkNode> { Folder(folderId, null, "Light Novels") };
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            var series = $"Small Batch Novel {i:D2}";
            nodes.Add(Bookmark(id, folderId, $"{series} Chapter 1", $"https://lightnovels.me/small-batch-{i}", position: i));
            fixture.NovelFull.SetTags(series, ["Fantasy"]);
        }

        await fixture.SeedAsync(nodes.ToArray());

        var summary = await fixture.Service.TagFolderAsync(
            folderId,
            forceRefresh: false,
            maxCandidates: 10,
            excludedBookmarkIds: [],
            CancellationToken.None);

        Assert.Equal(5, summary.ProcessedBookmarkIds.Count);
        Assert.False(summary.HasMore);
        Assert.Equal(0, summary.RemainingCandidates);
    }

    [Fact]
    public async Task TagFolderAsync_WhenCanceled_PersistsTagsCompletedBeforeCancel()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        await fixture.SeedAsync(
            Folder(folderId, null, "Light Novels"),
            Bookmark(firstId, folderId, "Solo Leveling Chapter 1", "https://lightnovels.me/solo-leveling-1"),
            Bookmark(secondId, folderId, "Tower of God Chapter 1", "https://lightnovels.me/tower-of-god-1"),
            Bookmark(thirdId, folderId, "Noblesse Chapter 1", "https://lightnovels.me/noblesse-1"));
        fixture.NovelFull.SetTags("Solo Leveling", ["Fantasy"]);
        fixture.NovelFull.SetTags("Tower of God", ["Action"]);
        fixture.NovelFull.SetTags("Noblesse", ["Drama"]);

        using var cts = new CancellationTokenSource();
        fixture.NovelFull.CancelAfterCalls = 3;
        fixture.NovelFull.CancellationSource = cts;

        var summary = await fixture.Service.TagFolderAsync(folderId, forceRefresh: false, cts.Token);

        Assert.Contains(summary.Messages, message => message.Contains("canceled", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(summary.Tagged, 1, 2);
        Assert.True(summary.HasMore);

        var tagged = await fixture.Db.BookmarkNodes
            .Where(node => node.ParentId == folderId && node.Tags != null)
            .Select(node => node.Id)
            .ToListAsync();
        Assert.Equal(summary.Tagged, tagged.Count);
    }

    [Fact]
    public async Task TagFolderAsync_SavesDirectlyToBookmarkNodeTagsAndUpdatedAt()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();
        var originalUpdatedAt = new DateTime(2024, 01, 01, 0, 0, 0, DateTimeKind.Utc);
        await fixture.SeedAsync(
            Folder(folderId, null, "Anime"),
            Bookmark(bookmarkId, folderId, "Frieren Episode 1", "https://crunchyroll.com/frieren", updatedAt: originalUpdatedAt));
        fixture.Anilist.SetTags("Frieren", ["Fantasy"]);

        await fixture.Service.TagFolderAsync(folderId, forceRefresh: false, CancellationToken.None);

        var bookmark = await fixture.Db.BookmarkNodes.SingleAsync(node => node.Id == bookmarkId);
        Assert.Equal("Anime,Fantasy", bookmark.Tags);
        Assert.True(bookmark.UpdatedAt > originalUpdatedAt);
    }

    [Fact]
    public async Task TagFolderAsync_DoesNotCreateExtensionCommandRows()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();
        await fixture.SeedAsync(
            Folder(folderId, null, "Anime"),
            Bookmark(bookmarkId, folderId, "One Piece Episode 1092", "https://crunchyroll.com/watch/one-piece"));
        fixture.Anilist.SetTags("One Piece", ["Shounen"]);

        await fixture.Service.TagFolderAsync(folderId, forceRefresh: false, CancellationToken.None);

        Assert.Empty(await fixture.Db.ExtensionCommands.ToListAsync());
    }

    [Fact]
    public async Task TagFolderAsync_ReturnsSummaryCounters()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var taggedId = Guid.NewGuid();
        var alreadyTaggedId = Guid.NewGuid();
        var lowConfidenceId = Guid.NewGuid();
        var noSourceTagsId = Guid.NewGuid();
        await fixture.SeedAsync(
            Folder(folderId, null, "General Folder"),
            Bookmark(taggedId, folderId, "One Piece Episode 1092", "https://example.com/one-piece"),
            Bookmark(alreadyTaggedId, folderId, "Naruto Episode 1", "https://example.com/naruto", tags: "Existing"),
            Bookmark(lowConfidenceId, folderId, "Unknown Episode 1", "https://example.com/unknown"),
            Bookmark(noSourceTagsId, folderId, "Empty Result Episode 1", "https://example.com/empty"));
        fixture.Identifier.EnqueueResult(taggedId, "One Piece", 0.91, AiSeriesSourceHint.Anime);
        fixture.Identifier.EnqueueResult(lowConfidenceId, "Unknown", 0.20, AiSeriesSourceHint.Anime);
        fixture.Identifier.EnqueueResult(noSourceTagsId, "Empty Result", 0.91, AiSeriesSourceHint.Anime);
        fixture.Anilist.SetTags("One Piece", ["Shounen"]);
        fixture.Anilist.SetTags("Empty Result", []);

        var summary = await fixture.Service.TagFolderAsync(folderId, forceRefresh: false, CancellationToken.None);

        Assert.Equal(4, summary.TotalCandidates);
        Assert.Equal(1, summary.Tagged);
        Assert.Equal(1, summary.SkippedAlreadyTagged);
        Assert.Equal(1, summary.SkippedLowConfidence);
        Assert.Equal(1, summary.SkippedNoSourceTags);
        Assert.Equal(0, summary.FailedChunks);
    }

    [Fact]
    public async Task TagFolderAsync_DoesNotMarkBookmarksProcessedWhenAiIdentificationFails()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await fixture.SeedAsync(
            Folder(folderId, null, "General Folder"),
            Bookmark(firstId, folderId, "Solo Leveling Chapter 1", "https://example.com/solo-leveling-1"),
            Bookmark(secondId, folderId, "One Piece Chapter 1", "https://example.com/one-piece-1"));
        fixture.Identifier.FailWithStatus(HttpStatusCode.ServiceUnavailable);

        var summary = await fixture.Service.TagFolderAsync(
            folderId,
            forceRefresh: false,
            maxCandidates: 2,
            excludedBookmarkIds: [],
            CancellationToken.None);

        Assert.Equal(1, summary.FailedChunks);
        Assert.Empty(summary.ProcessedBookmarkIds);
        Assert.True(summary.HasMore);
    }

    [Fact]
    public async Task TagFolderAsync_BypassesAiForDeterministicCandidates()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();
        await fixture.SeedAsync(
            Folder(folderId, null, "Novels"),
            Bookmark(bookmarkId, folderId, "A Monster Who Levels Up Chapter 1", "https://example.com/novel"));
        fixture.NovelFull.SetTags("A Monster Who Levels Up", ["Fantasy", "Level System"]);

        var summary = await fixture.Service.TagFolderAsync(folderId, forceRefresh: false, CancellationToken.None);

        Assert.Equal(1, summary.TotalCandidates);
        Assert.Equal(1, summary.Tagged);
        Assert.Equal(0, fixture.Identifier.RequestCount); // Bypassed AI
        var status = Assert.Single(summary.BookmarkStatuses);
        Assert.Equal(bookmarkId, status.BookmarkId);
        Assert.Equal("DeterministicClassified", status.Status);
    }

    [Fact]
    public async Task TagFolderAsync_RateLimitedFailsChunksAndSetsStopForRateLimit()
    {
        await using var fixture = await AiAutoTagFixture.CreateAsync();
        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();
        await fixture.SeedAsync(
            Folder(folderId, null, "General Folder"),
            Bookmark(bookmarkId, folderId, "Solo Leveling Chapter 1", "https://example.com/solo-leveling-1"));
        fixture.Identifier.FailWithStatus(HttpStatusCode.TooManyRequests); // AI client returns RateLimit

        var summary = await fixture.Service.TagFolderAsync(folderId, forceRefresh: false, CancellationToken.None);

        Assert.True(summary.StopForRateLimit);
        Assert.Equal(1, summary.RateLimited);
        Assert.Equal(1, summary.PendingRetry);
        Assert.Equal(2, summary.RetryAfterSeconds);
        Assert.Empty(summary.ProcessedBookmarkIds);

        var status = Assert.Single(summary.BookmarkStatuses);
        Assert.Equal(bookmarkId, status.BookmarkId);
        Assert.Equal("RateLimited", status.Status);
    }

    private static BookmarkNode Folder(Guid id, Guid? parentId, string title)
        => new()
        {
            Id = id,
            ParentId = parentId,
            Type = NodeType.Folder,
            Title = title,
            Position = 0,
            Version = 1,
            UpdatedAt = DateTime.UtcNow
        };

    private static BookmarkNode Bookmark(Guid id, Guid parentId, string title, string url, string? tags = null, DateTime? updatedAt = null, int position = 0)
        => new()
        {
            Id = id,
            ParentId = parentId,
            Type = NodeType.Bookmark,
            Title = title,
            Url = url,
            Position = position,
            SyncState = SyncState.Synced,
            Version = 1,
            Tags = tags,
            UpdatedAt = updatedAt ?? DateTime.UtcNow
        };

    private sealed class AiAutoTagFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private AiAutoTagFixture(SqliteConnection connection, AppDbContext db, QueueHttpMessageHandler identifier)
        {
            _connection = connection;
            Db = db;
            Identifier = identifier;
            Anilist = new FakeProvider();
            MangaUpdates = new FakeProvider();
            Kitsu = new FakeProvider();
            NovelFull = new FakeProvider();
            var identifierService = new AiSeriesIdentifierService(new HttpClient(identifier), new Uri("https://ai.local/identify"));
            Service = new AiBookmarkAutoTaggingService(
                Db,
                identifierService,
                Anilist,
                MangaUpdates,
                Kitsu,
                NovelFull,
                NullLogger<AiBookmarkAutoTaggingService>.Instance);
        }

        public AppDbContext Db { get; }
        public QueueHttpMessageHandler Identifier { get; }
        public FakeProvider Anilist { get; }
        public FakeProvider MangaUpdates { get; }
        public FakeProvider Kitsu { get; }
        public FakeProvider NovelFull { get; }
        public AiBookmarkAutoTaggingService Service { get; }

        public static async Task<AiAutoTagFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new AiAutoTagFixture(connection, db, new QueueHttpMessageHandler());
        }

        public async Task SeedAsync(params BookmarkNode[] nodes)
        {
            Db.BookmarkNodes.AddRange(nodes);
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeProvider : IAnilistTagProvider, IMangaUpdatesTagProvider, IKitsuTagProvider, INovelFullTagProvider
    {
        private readonly Dictionary<string, List<string>> _tagsByTitle = new(StringComparer.OrdinalIgnoreCase);

        public int CallCount { get; private set; }
        public int? CancelAfterCalls { get; set; }
        public CancellationTokenSource? CancellationSource { get; set; }

        public void SetTags(string title, List<string> tags) => _tagsByTitle[title] = tags;

        public Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            if (CancelAfterCalls is int threshold && CallCount >= threshold && CancellationSource is not null)
                CancellationSource.Cancel();

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new ProviderTagResult(
                _tagsByTitle.TryGetValue(context.OriginalTitle, out var tags) ? tags : [],
                WasRejected: false,
                RejectionReason: null));
        }
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<Guid, AiSeriesIdentification> _items = new();
        private HttpStatusCode? _failureStatus;

        public int RequestCount { get; private set; }

        public void EnqueueResult(Guid id, string canonicalTitle, double confidence, AiSeriesSourceHint sourceHint)
            => _items[id] = new AiSeriesIdentification(id, canonicalTitle, confidence, sourceHint);

        public void EnqueueIdentification(Guid id, string canonicalTitle, double confidence, AiSeriesSourceHint sourceHint)
            => EnqueueResult(id, canonicalTitle, confidence, sourceHint);

        public void FailWithStatus(HttpStatusCode statusCode) => _failureStatus = statusCode;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (_failureStatus is { } statusCode)
            {
                var response = new HttpResponseMessage(statusCode);
                if (statusCode == HttpStatusCode.TooManyRequests)
                {
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                }
                return response;
            }

            var requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            var sentIds = System.Text.RegularExpressions.Regex.Matches(
                    requestJson,
                    "\\\"id\\\"\\s*:\\s*\\\"(?<id>[^\\\"]+)\\\"")
                .Select(match => Guid.Parse(match.Groups["id"].Value))
                .ToList();
            var items = sentIds.Select(id =>
            {
                var next = _items.TryGetValue(id, out var val) ? val : new AiSeriesIdentification(id, "Unknown", 0.5, AiSeriesSourceHint.Unknown);
                return $"{{ \"id\": \"{next.Id}\", \"canonicalTitle\": \"{next.CanonicalTitle}\", \"confidence\": {next.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"sourceHint\": \"{next.SourceHint}\" }}";
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{ \"items\": [ {string.Join(",", items)} ] }}")
            };
        }
    }
}
