using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.Contracts;
using BookmarkManager.UnitTests.UrlMigration.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests.UrlMigration;

public sealed class TargetHostDiscoveryServiceTests
{
    private const string DeadHost = "www.webtoon.xyz";

    private static BookmarkNode Bookmark(string title, string url) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Url = url,
        Type = NodeType.Bookmark,
        SyncState = SyncState.Synced,
        UpdatedAt = DateTime.UtcNow
    };

    // ---------------------------------------------------------------- sampling

    [Fact]
    public void SelectSample_PicksDistinctSeriesSpreadAcrossList_AndSkipsUnparseable()
    {
        var bookmarks = new List<BookmarkNode>
        {
            Bookmark("Series 0", "https://www.webtoon.xyz/read/series-0/chapter-1/"),
            Bookmark("Series 0 alt", "https://www.webtoon.xyz/read/series-0/chapter-2/"), // duplicate slug
            Bookmark("Unparseable", "https://www.webtoon.xyz/about"),                       // no slug+chapter
            Bookmark("Series 1", "https://www.webtoon.xyz/read/series-1/chapter-1/"),
            Bookmark("Series 2", "https://www.webtoon.xyz/read/series-2/chapter-1/"),
            Bookmark("Series 3", "https://www.webtoon.xyz/read/series-3/chapter-1/"),
            Bookmark("Series 4", "https://www.webtoon.xyz/read/series-4/chapter-1/"),
            Bookmark("Series 5", "https://www.webtoon.xyz/read/series-5/chapter-1/"),
            Bookmark("Series 6", "https://www.webtoon.xyz/read/series-6/chapter-1/"),
            Bookmark("Series 7", "https://www.webtoon.xyz/read/series-7/chapter-1/"),
            Bookmark("Series 8", "https://www.webtoon.xyz/read/series-8/chapter-1/"),
            Bookmark("Series 9", "https://www.webtoon.xyz/read/series-9/chapter-1/"),
            Bookmark("Series 10", "https://www.webtoon.xyz/read/series-10/chapter-1/"),
            Bookmark("Series 11", "https://www.webtoon.xyz/read/series-11/chapter-1/")
        };

        var sample = TargetHostDiscoveryService.SelectSample(bookmarks, 5);

        Assert.Equal(5, sample.Count);
        Assert.Equal(5, sample.Select(s => s.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(sample, s => s.Slug == "about");
        // Even spread, not the first five: the last distinct series must be included.
        Assert.Equal("series-0", sample[0].Slug);
        Assert.Equal("series-11", sample[^1].Slug);
    }

    [Fact]
    public void SelectSample_ClampsToMinimumFive()
    {
        var bookmarks = Enumerable.Range(0, 12)
            .Select(i => Bookmark($"Series {i}", $"https://www.webtoon.xyz/read/series-{i}/chapter-1/"))
            .ToList();

        Assert.Equal(5, TargetHostDiscoveryService.SelectSample(bookmarks, 1).Count);
    }

    // ---------------------------------------------------------------- discovery

    [Fact]
    public async Task Discover_UsesSearxngFirst_AndTavilyOnlyWhenSearxngHasNoUsableHost()
    {
        using var harness = await Harness.CreateAsync(
            titles: ["Searxng One", "Searxng Two", "Tavily One", "Tavily Two", "Tavily Three"],
            searxng: extraction => extraction.SeriesName.StartsWith("Searxng", StringComparison.Ordinal)
                ? [new("https://free.example/series/x", null, null)]
                : [],
            tavily: _ => [new("https://paid.example/series/x/chapter-1", null, null)],
            verification: _ => new VerificationResult(true, true, true, "matched"),
            tavilyApiKey: "test-key");

        var result = await harness.Service.DiscoverAsync(DeadHost, 5, default);

        Assert.Equal(5, harness.Searxng.Calls);
        Assert.Equal(3, harness.Tavily.Calls);
        Assert.Equal(2, result.Suggestions.Select(s => s.Host).Distinct().Count());
        var free = Assert.Single(result.Suggestions, s => s.Host == "free.example");
        var paid = Assert.Single(result.Suggestions, s => s.Host == "paid.example");
        Assert.Equal(0, free.SearchCreditsUsed);
        // Three series needed Tavily, each surfaced paid.example.
        Assert.Equal(3, paid.SearchCreditsUsed);
    }

    [Fact]
    public async Task Discover_ExcludesDeadHostNoiseAndPrivateAddresses()
    {
        using var harness = await Harness.CreateAsync(
            titles: ["One", "Two", "Three", "Four", "Five"],
            searxng: _ =>
            [
                new("https://mangaupdates.com/series/x", null, null),
                new("https://myanimelist.net/anime/1", null, null),
                new("http://127.0.0.1/admin", null, null),
                new("http://192.168.1.100/router", null, null),
                new($"https://{DeadHost}/read/series-0/chapter-1/", null, null),
                new("https://reader.example/series/x", null, null)
            ],
            tavily: _ => [],
            verification: _ => new VerificationResult(true, true, true, "matched"),
            tavilyApiKey: null);

        var result = await harness.Service.DiscoverAsync(DeadHost, 5, default);

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal("reader.example", suggestion.Host);
        Assert.DoesNotContain(result.Suggestions, s => s.Host.Contains("mangaupdates"));
        Assert.DoesNotContain(result.Suggestions, s => s.Host.Contains("myanimelist"));
        Assert.DoesNotContain(result.Suggestions, s => s.Host.Contains("127.0.0.1"));
        Assert.DoesNotContain(result.Suggestions, s => s.Host.Contains("192.168.1.100"));
        Assert.DoesNotContain(result.Suggestions, s => s.Host.Contains(DeadHost));
    }

    [Fact]
    public async Task Discover_RanksHostWithMostChaptersFirst_AndReportsBestPattern()
    {
        using var harness = await Harness.CreateAsync(
            titles: ["One", "Two", "Three", "Four", "Five"],
            searxng: extraction => extraction.SeriesName is "One" or "Two" or "Three"
                ? [new("https://host-a.example/", null, null)]
                : [new("https://host-b.example/", null, null)],
            tavily: _ => [],
            verification: candidate =>
            {
                var uri = new Uri(candidate.Url);
                var host = uri.Host;
                var slug = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(segment => segment.StartsWith("series-", StringComparison.OrdinalIgnoreCase));
                if (slug is null || !int.TryParse(slug["series-".Length..], out var index))
                {
                    return new VerificationResult(false, false, false, "n/a");
                }

                var isChapterUrl = candidate.Url.Contains("chapter", StringComparison.OrdinalIgnoreCase);
                if (host == "host-a.example" && index <= 2)
                {
                    return new VerificationResult(true, true, isChapterUrl, "matched");
                }

                if (host == "host-b.example" && index >= 3)
                {
                    // Series pages verify; no chapter template ever does.
                    return new VerificationResult(true, true, false, "series only");
                }

                return new VerificationResult(false, false, false, "n/a");
            },
            tavilyApiKey: null);

        var result = await harness.Service.DiscoverAsync(DeadHost, 5, default);

        Assert.Equal(2, result.Suggestions.Count);
        var first = result.Suggestions[0];
        var second = result.Suggestions[1];
        Assert.Equal("host-a.example", first.Host);
        Assert.Equal(3, first.ChaptersFound);
        Assert.Equal(3, first.SeriesFound);
        Assert.Equal("/{slug}/chapter-{n}", first.BestPattern);
        Assert.Equal("host-b.example", second.Host);
        Assert.Equal(0, second.ChaptersFound);
        Assert.Equal(2, second.SeriesFound);
        Assert.Null(second.BestPattern);
    }

    [Fact]
    public async Task Discover_StopsProbingHostAfterEightConsecutiveSeriesFailures()
    {
        var probedSlugs = new ConcurrentDictionary<string, byte>();
        var titles = Enumerable.Range(0, 12).Select(i => $"Series {i}").ToList();

        using var harness = await Harness.CreateAsync(
            titles: titles,
            searxng: _ => [new("https://fail.example/", null, null)],
            tavily: _ => [],
            verification: candidate =>
            {
                var host = new Uri(candidate.Url).Host;
                if (host == "fail.example")
                {
                    var slug = new Uri(candidate.Url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(segment => segment.StartsWith("series-", StringComparison.OrdinalIgnoreCase));
                    if (slug != null)
                    {
                        probedSlugs.TryAdd(slug, 0);
                    }
                }

                return new VerificationResult(false, false, false, "n/a");
            },
            tavilyApiKey: null);

        var result = await harness.Service.DiscoverAsync(DeadHost, 50, default);

        // Every host failed, so nothing is suggested...
        Assert.Empty(result.Suggestions);
        Assert.NotNull(result.Detail);
        // ...and the host is abandoned after the first eight series (not all twelve).
        Assert.Equal(TargetHostDiscoveryService.EarlyStopFailures, probedSlugs.Count);
    }

    [Fact]
    public async Task Discover_KeepsAtMostFiveHosts()
    {
        using var harness = await Harness.CreateAsync(
            titles: Enumerable.Range(0, 7).Select(i => $"Series {i}").ToList(),
            searxng: extraction =>
            {
                var index = int.Parse(extraction.SeriesName["Series ".Length..]);
                return [new($"https://host-{index}.example/", null, null)];
            },
            tavily: _ => [],
            verification: _ => new VerificationResult(true, true, true, "matched"),
            tavilyApiKey: null);

        var result = await harness.Service.DiscoverAsync(DeadHost, 50, default);

        Assert.Equal(TargetHostDiscoveryService.MaxHosts, result.Suggestions.Count);
    }

    [Fact]
    public async Task Discover_NoQualifyingHost_SetsDetailAndNoSuggestions()
    {
        using var harness = await Harness.CreateAsync(
            titles: ["One", "Two", "Three", "Four", "Five"],
            searxng: _ => [],
            tavily: _ => [],
            verification: _ => new VerificationResult(false, false, false, "n/a"),
            tavilyApiKey: null);

        var result = await harness.Service.DiscoverAsync(DeadHost, 5, default);

        Assert.Empty(result.Suggestions);
        Assert.NotNull(result.Detail);
        Assert.Equal(5, result.SampleSize);
    }

    [Fact]
    public async Task Discover_BudgetExpiry_ReturnsPartialRankedResults_WithoutThrowing()
    {
        using var harness = await Harness.CreateAsync(
            titles: ["One", "Two", "Three", "Four", "Five"],
            searxng: extraction => extraction.SeriesName is "One" or "Two" or "Three"
                ? [new("https://host-a.example/", null, null)]
                : [new("https://host-b.example/", null, null)],
            tavily: _ => [],
            verification: null,
            tavilyApiKey: null,
            discoveryBudget: TimeSpan.FromMilliseconds(150),
            asyncVerification: async (candidate, token) =>
            {
                var uri = new Uri(candidate.Url);
                var slug = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(segment => segment.StartsWith("series-", StringComparison.OrdinalIgnoreCase));
                var index = slug is null ? -1 : int.Parse(slug["series-".Length..]);

                if (uri.Host == "host-b.example")
                {
                    // Slow host: the overall budget must cut this off instead of letting the
                    // caller's HttpClient hit its own timeout.
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }

                var matches = (uri.Host == "host-a.example" && index <= 2) ||
                              (uri.Host == "host-b.example" && index >= 3);
                return matches
                    ? new VerificationResult(true, true,
                        candidate.Url.Contains("chapter", StringComparison.OrdinalIgnoreCase), "matched")
                    : new VerificationResult(false, false, false, "n/a");
            });

        var result = await harness.Service.DiscoverAsync(DeadHost, 5, default);

        Assert.NotNull(result);
        Assert.Equal(TargetHostDiscoveryService.PartialDetail, result.Detail);
        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal("host-a.example", suggestion.Host);
        Assert.Equal(3, suggestion.SeriesFound);
    }

    [Fact]
    public async Task Discover_SearchesOverlap_WithBoundedConcurrency()
    {
        using var harness = await Harness.CreateAsync(
            titles: Enumerable.Range(0, 9).Select(i => $"Series {i}").ToList(),
            searxng: _ => [new("https://reader.example/", null, null)],
            tavily: _ => [],
            verification: _ => new VerificationResult(true, true, false, "series only"),
            tavilyApiKey: null);
        harness.Searxng.Delay = TimeSpan.FromMilliseconds(80);

        var result = await harness.Service.DiscoverAsync(DeadHost, 50, default);

        Assert.True(harness.Searxng.MaxInFlight >= 2, $"expected overlapping searches, saw {harness.Searxng.MaxInFlight}");
        Assert.True(
            harness.Searxng.MaxInFlight <= TargetHostDiscoveryService.MaxSearchConcurrency,
            $"concurrency {harness.Searxng.MaxInFlight} exceeded {TargetHostDiscoveryService.MaxSearchConcurrency}");
        Assert.Equal(9, harness.Searxng.Calls);
        Assert.Single(result.Suggestions, s => s.Host == "reader.example");
    }

    [Fact]
    public async Task Discover_CreditCountExactUnderConcurrency()
    {
        using var harness = await Harness.CreateAsync(
            titles: Enumerable.Range(0, 9).Select(i => $"Series {i}").ToList(),
            searxng: _ => [],
            tavily: _ => [new("https://paid.example/series/x", null, null)],
            verification: _ => new VerificationResult(true, true, false, "series only"),
            tavilyApiKey: "test-key");

        var result = await harness.Service.DiscoverAsync(DeadHost, 50, default);

        Assert.Equal(9, harness.Searxng.Calls);
        Assert.Equal(9, harness.Tavily.Calls);
        Assert.Equal(9, result.SearchCreditsUsed);
        var suggestion = Assert.Single(result.Suggestions, s => s.Host == "paid.example");
        Assert.Equal(9, suggestion.SearchCreditsUsed);
    }

    // ---------------------------------------------------------------- harness

    private sealed class Harness : IDisposable
    {
        public required AppDbContext Db { get; init; }
        public required TargetHostDiscoveryService Service { get; init; }
        public required FakeSearxng Searxng { get; init; }
        public required FakeTavily Tavily { get; init; }
        public required string DatabasePath { get; init; }

        public static async Task<Harness> CreateAsync(
            IReadOnlyList<string> titles,
            Func<SeriesExtraction, IReadOnlyList<SearchCandidate>> searxng,
            Func<SeriesExtraction, IReadOnlyList<SearchCandidate>> tavily,
            Func<SearchCandidate, VerificationResult>? verification,
            string? tavilyApiKey,
            TimeSpan? discoveryBudget = null,
            Func<SearchCandidate, CancellationToken, Task<VerificationResult>>? asyncVerification = null)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"host-discovery-{Guid.NewGuid():N}.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            for (var i = 0; i < titles.Count; i++)
            {
                db.BookmarkNodes.Add(Bookmark(titles[i], $"https://{DeadHost}/read/series-{i}/chapter-51/"));
            }

            await db.SaveChangesAsync();

            var searxngFake = new FakeSearxng(searxng);
            var tavilyFake = new FakeTavily(tavily);
            var settings = new InMemoryAiTaggingSettingsService(new AiTaggingSettingsDto { TavilyApiKey = tavilyApiKey });
            ICandidateVerificationService verificationDouble = asyncVerification is not null
                ? new AsyncStubVerification(asyncVerification)
                : new StubVerification(verification ?? (_ => new VerificationResult(false, false, false, "n/a")));

            var service = new TargetHostDiscoveryService(
                db,
                new StubExtraction(),
                searxngFake,
                tavilyFake,
                settings,
                verificationDouble,
                NullLogger<TargetHostDiscoveryService>.Instance,
                configuration: null,
                discoveryBudget: discoveryBudget);

            return new Harness { Db = db, Service = service, Searxng = searxngFake, Tavily = tavilyFake, DatabasePath = path };
        }

        public void Dispose()
        {
            Db.Dispose();
            if (File.Exists(DatabasePath))
            {
                File.Delete(DatabasePath);
            }
        }
    }

    private sealed class StubExtraction : ISeriesExtractionService
    {
        public Task<SeriesExtraction> ExtractAsync(string title, string url, string? category, CancellationToken ct)
            => Task.FromResult(new SeriesExtraction(title, "51", "manga", true));
    }

    private sealed class StubVerification(Func<SearchCandidate, VerificationResult> verify) : ICandidateVerificationService
    {
        public Task<VerificationResult> VerifyAsync(SearchCandidate candidate, SeriesExtraction extraction, CancellationToken ct)
            => Task.FromResult(verify(candidate));

        public Task<IReadOnlyList<string>> DiscoverPageLinksAsync(string seriesPageUrl, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class AsyncStubVerification(Func<SearchCandidate, CancellationToken, Task<VerificationResult>> verify) : ICandidateVerificationService
    {
        public Task<VerificationResult> VerifyAsync(SearchCandidate candidate, SeriesExtraction extraction, CancellationToken ct)
            => verify(candidate, ct);

        public Task<IReadOnlyList<string>> DiscoverPageLinksAsync(string seriesPageUrl, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class FakeSearxng(Func<SeriesExtraction, IReadOnlyList<SearchCandidate>> responder) : ISearxngSearchService
    {
        public int Calls;
        public int MaxInFlight;
        public int Active;

        /// <summary>Artificial per-call latency so a concurrency test can observe overlap.</summary>
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public async Task<SearchOutcome<SearchCandidate>> SearchWithDiagnosticsAsync(
            SeriesExtraction extraction, string deadHost, SearchRunContext run, CancellationToken ct,
            string? preferredHost = null, bool restrictToPreferredHost = false, string? queryOverride = null)
        {
            Interlocked.Increment(ref Calls);
            var active = Interlocked.Increment(ref Active);
            UpdateMax(active);
            try
            {
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, ct).ConfigureAwait(false);
                }

                var candidates = responder(extraction);
                return new SearchOutcome<SearchCandidate>(candidates, [new("SearXNG", candidates.Count, null)]);
            }
            finally
            {
                Interlocked.Decrement(ref Active);
            }
        }

        private void UpdateMax(int active)
        {
            int current;
            while (active > (current = Volatile.Read(ref MaxInFlight)))
            {
                Interlocked.CompareExchange(ref MaxInFlight, active, current);
            }
        }
    }

    private sealed class FakeTavily(Func<SeriesExtraction, IReadOnlyList<SearchCandidate>> responder) : ITavilySearchService
    {
        public int Calls;

        public Task<SearchOutcome<SearchCandidate>> SearchWithDiagnosticsAsync(
            SeriesExtraction extraction, string deadHost, SearchRunContext run, CancellationToken ct,
            string? preferredHost = null, bool restrictToPreferredHost = false, string? queryOverride = null)
        {
            Interlocked.Increment(ref Calls);
            var candidates = responder(extraction);
            return Task.FromResult(new SearchOutcome<SearchCandidate>(candidates, [new("Tavily", candidates.Count, null)]));
        }
    }
}
