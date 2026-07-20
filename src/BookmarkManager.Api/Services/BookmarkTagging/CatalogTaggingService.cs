using System.Collections.Concurrent;
using System.Diagnostics;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.BookmarkTagging;

/// <summary>Offline Novel-domain tag lookup against the local <see cref="LibraryCatalogEntry"/> mirror
/// (populated by <see cref="LibraryCatalogSyncBackgroundService"/> crawling Novelfire/RanobeDB etc.),
/// indexing LightNovel/Webnovel/Manhwa rows (many KR webnovels exist in the catalog only as Manhwa
/// rows with identical titles), so bookmarks matching a title already in the catalog get tagged
/// without any live HTTP call. Runs
/// as a singleton alongside the other tag providers, so DB access goes through a fresh scope per
/// lookup instead of a directly injected (scoped) <see cref="AppDbContext"/> - same pattern as
/// <see cref="Library.LibraryProviderRegistry"/>.</summary>
public sealed class CatalogTaggingService : ICatalogTagProvider
{
    private const double SimilarityThreshold = SimilarityThresholds.Catalog;
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan EmptyCacheDuration = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan DefaultIndexTtl = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LibraryProviderRegistry _registry;
    private readonly ILogger<CatalogTaggingService> _logger;
    private readonly TimeSpan _indexTtl;
    private readonly ConcurrentDictionary<string, CatalogCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _indexBuildLock = new(1, 1);
    private volatile CatalogIndexSnapshot? _index;

    private sealed record CatalogCacheEntry(ProviderTagResult Result, DateTimeOffset ExpiresAt);

    /// <summary>One catalog row's precomputed title token sets (main title + each alternate title),
    /// so lookups score against a pre-tokenized in-memory pool instead of an EF LIKE prefilter -
    /// stored titles keep punctuation ("Max-Level Learning Ability: ...") that LIKE-on-a-stripped-query
    /// can never match as a contiguous substring.</summary>
    private sealed record CatalogIndexEntry(Guid Id, List<HashSet<string>> TitleTokenSets);

    private sealed record CatalogIndexSnapshot(IReadOnlyList<CatalogIndexEntry> Entries, DateTimeOffset BuiltAt);

    /// <summary>Diagnostic result for <see cref="ExplainTopMatchesAsync"/>: one catalog entry's best
    /// score against a query, with the full breakdown of how that score was computed.</summary>
    internal sealed record CatalogMatchExplain(Guid EntryId, string Title, double Score, TokenSetScoreBreakdown Breakdown);

    public CatalogTaggingService(
        IServiceScopeFactory scopeFactory,
        LibraryProviderRegistry registry,
        ILogger<CatalogTaggingService> logger)
        : this(scopeFactory, registry, logger, DefaultIndexTtl)
    {
    }

    /// <summary>Test-only seam for exercising index TTL expiry without waiting an hour.</summary>
    internal CatalogTaggingService(
        IServiceScopeFactory scopeFactory,
        LibraryProviderRegistry registry,
        ILogger<CatalogTaggingService> logger,
        TimeSpan indexTtl)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _logger = logger;
        _indexTtl = indexTtl;
    }

    public async Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken)
    {
        if (context.Domain != BookmarkTagDomain.Novel)
            return new([], false, null);

        var candidate = context.NormalizedTitle.Candidates.FirstOrDefault()?.Query ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate) || MediaTitleNormalizer.NormalizeForSearch(candidate).Length < 2)
            return new([], false, null);

        var now = DateTimeOffset.UtcNow;
        var cacheKey = $"{context.Domain}:{candidate}";
        if (!context.BypassCache && _cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
        {
            ProviderAutoTagTelemetry.RecordCacheHit("Catalog");
            return cached.Result;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await LookupCatalogAsync(candidate, cancellationToken).ConfigureAwait(false);
            ProviderAutoTagTelemetry.RecordHttp("Catalog", "lookup", stopwatch.Elapsed);
            _cache[cacheKey] = new CatalogCacheEntry(result, now.Add(result.Tags.Count == 0 ? EmptyCacheDuration : SuccessCacheDuration));
            return result;
        }
        catch (Exception ex)
        {
            ProviderAutoTagTelemetry.RecordFailure("Catalog", "lookup");
            _logger.LogWarning(ex, "Failed to query local catalog for tags of '{Title}'", context.OriginalTitle);
            var emptyResult = new ProviderTagResult([], false, null);
            _cache[cacheKey] = new CatalogCacheEntry(emptyResult, now.Add(EmptyCacheDuration));
            return emptyResult;
        }
    }

    private async Task<ProviderTagResult> LookupCatalogAsync(string candidate, CancellationToken cancellationToken)
    {
        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index.Entries.Count == 0)
            return new([], false, null);

        var queryTokens = MediaTitleNormalizer.NormalizeForSearch(candidate)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (queryTokens.Count == 0)
            return new([], false, null);

        var bestScore = -1.0;
        var bestId = Guid.Empty;
        foreach (var entry in index.Entries)
        {
            foreach (var titleTokens in entry.TitleTokenSets)
            {
                var score = MediaTitleNormalizer.ScoreTokenSets(queryTokens, titleTokens);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = entry.Id;
                }
            }
        }

        if (bestId == Guid.Empty || bestScore < SimilarityThreshold)
            return new([], false, $"Best candidate similarity ({bestScore:F4}) was below similarity threshold {SimilarityThreshold:F2}.");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var best = await db.LibraryCatalogEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == bestId, cancellationToken)
            .ConfigureAwait(false);
        if (best is null)
            return new([], false, null);

        var genres = LibraryCatalogEntry.SplitList(best.Genres);
        if (genres.Count == 0)
            genres = await TryEnrichGenresAsync(db, best, cancellationToken).ConfigureAwait(false);

        if (genres.Count == 0)
            return new([], false, null);

        var tags = new List<string> { "Novel" };
        tags.AddRange(genres);
        var canonical = string.IsNullOrWhiteSpace(best.Title) ? null : best.Title.Trim();
        return new(tags, false, null, canonical, bestScore);
    }

    /// <summary>Returns the cached in-memory title index, rebuilding it when missing or past its TTL.
    /// Note: <see cref="MediaTagLookupContext.BypassCache"/> deliberately does NOT force a rebuild here -
    /// it only bypasses the per-title result cache in <see cref="GetTagsForTitleAsync"/>. Rebuilding the
    /// ~34k-row index on every lookup would be far too expensive; the catalog only grows gradually via
    /// the background crawl, so serving a slightly stale index is an acceptable tradeoff.</summary>
    private async Task<CatalogIndexSnapshot> GetIndexAsync(CancellationToken cancellationToken)
    {
        var current = _index;
        if (current is not null && DateTimeOffset.UtcNow - current.BuiltAt < _indexTtl)
            return current;

        // No index yet: every caller must block for the first build. Once one exists, only one
        // thread rebuilds at a time (non-blocking try) while everyone else keeps serving the
        // still-valid-enough stale snapshot instead of queueing behind the rebuild.
        var acquired = await _indexBuildLock.WaitAsync(current is null ? Timeout.Infinite : 0, cancellationToken).ConfigureAwait(false);
        if (!acquired)
            return current!;

        try
        {
            current = _index;
            if (current is not null && DateTimeOffset.UtcNow - current.BuiltAt < _indexTtl)
                return current;

            var built = await BuildIndexAsync(cancellationToken).ConfigureAwait(false);
            _index = built;
            return built;
        }
        finally
        {
            _indexBuildLock.Release();
        }
    }

    private async Task<CatalogIndexSnapshot> BuildIndexAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Include Manhwa rows: many KR webnovels exist in the catalog only as Manhwa rows with
        // identical titles ("Omniscient Reader's Viewpoint" is MangaDex/Manhwa; the novel bookmark
        // could otherwise never match). ~900 Manhwa rows - negligible index size impact.
        var rows = await db.LibraryCatalogEntries
            .AsNoTracking()
            .Where(e => e.MediaType == LibraryMediaType.LightNovel || e.MediaType == LibraryMediaType.Webnovel || e.MediaType == LibraryMediaType.Manhwa)
            .Select(e => new { e.Id, e.Title, e.AlternateTitles })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var entries = new List<CatalogIndexEntry>(rows.Count);
        foreach (var row in rows)
        {
            var alternates = LibraryCatalogEntry.SplitList(row.AlternateTitles);
            var titleTokenSets = new List<HashSet<string>> { TokenizeTitle(row.Title) };
            foreach (var alternateTitle in alternates)
                titleTokenSets.Add(TokenizeTitle(alternateTitle));
            AddRecombinedCommaFragments(titleTokenSets, alternates);

            titleTokenSets.RemoveAll(set => set.Count == 0);
            if (titleTokenSets.Count > 0)
                entries.Add(new CatalogIndexEntry(row.Id, titleTokenSets));
        }

        return new CatalogIndexSnapshot(entries, DateTimeOffset.UtcNow);
    }

    // AlternateTitles is comma-joined, so an alternate title that itself CONTAINS commas
    // ("Myst, Might, Mayhem") arrives shredded into single-token fragments that can never
    // score above ~1/n against the full query. Heuristic repair: every run of >=2 consecutive
    // short fragments (<=2 tokens each) also gets one combined token set representing the
    // possibility that the run was originally a single comma-containing title. The individual
    // fragments stay in the index, so genuinely separate one-word alternates keep matching.
    private static void AddRecombinedCommaFragments(List<HashSet<string>> titleTokenSets, List<string> alternates)
    {
        const int maxFragmentTokens = 2;
        const int maxCombinedTokens = 8;

        var run = new HashSet<string>(StringComparer.Ordinal);
        var runLength = 0;

        void FlushRun()
        {
            if (runLength >= 2 && run.Count > 0 && run.Count <= maxCombinedTokens)
                titleTokenSets.Add(new HashSet<string>(run, StringComparer.Ordinal));
            run.Clear();
            runLength = 0;
        }

        foreach (var alternate in alternates)
        {
            var tokens = TokenizeTitle(alternate);
            if (tokens.Count is > 0 and <= maxFragmentTokens)
            {
                run.UnionWith(tokens);
                runLength++;
            }
            else
            {
                FlushRun();
            }
        }

        FlushRun();
    }

    private static HashSet<string> TokenizeTitle(string title)
        => MediaTitleNormalizer.NormalizeForSearch(title)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Diagnostic-only counterpart to <see cref="LookupCatalogAsync"/>: instead of returning
    /// only the single best-scoring entry, returns the full score breakdown for the top N entries so
    /// callers can inspect why a match did or did not clear the similarity threshold. Reuses the same
    /// in-memory index; does not read or write the per-title result <see cref="_cache"/>, since this
    /// is not part of <see cref="GetTagsForTitleAsync"/>'s cache lifecycle.</summary>
    internal async Task<List<CatalogMatchExplain>> ExplainTopMatchesAsync(string candidateQuery, int topN, CancellationToken cancellationToken)
    {
        topN = Math.Clamp(topN, 1, 25);

        var queryTokens = MediaTitleNormalizer.NormalizeForSearch(candidateQuery)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (queryTokens.Count == 0)
            return [];

        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index.Entries.Count == 0)
            return [];

        var bestByEntry = new List<(Guid Id, TokenSetScoreBreakdown Breakdown)>();
        foreach (var entry in index.Entries)
        {
            TokenSetScoreBreakdown? best = null;
            foreach (var titleTokens in entry.TitleTokenSets)
            {
                var breakdown = MediaTitleNormalizer.ExplainTokenSets(queryTokens, titleTokens);
                if (best is null || breakdown.Score > best.Score)
                    best = breakdown;
            }

            if (best is not null)
                bestByEntry.Add((entry.Id, best));
        }

        var top = bestByEntry
            .OrderByDescending(e => e.Breakdown.Score)
            .Take(topN)
            .ToList();

        var titlesById = new Dictionary<Guid, string>();
        if (top.Count > 0)
        {
            try
            {
                var idsToFetch = top.Select(e => e.Id).ToList();
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var titles = await db.LibraryCatalogEntries
                    .AsNoTracking()
                    .Where(e => idsToFetch.Contains(e.Id))
                    .Select(e => new { e.Id, e.Title })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var title in titles)
                    titlesById[title.Id] = title.Title;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to fetch catalog titles for tag-explain diagnostics.");
            }
        }

        return top
            .Select(e => new CatalogMatchExplain(e.Id, titlesById.GetValueOrDefault(e.Id, string.Empty), e.Breakdown.Score, e.Breakdown))
            .ToList();
    }

    /// <summary>Mirrors <see cref="LibrarySearchService.EnrichEntryAsync"/>'s on-demand single-entry
    /// enrichment: fetch the provider's detail page for a catalog row the bulk crawl only had thin
    /// listing data for, merge it back via the shared <see cref="LibraryCatalogSyncBackgroundService.ApplyDto"/>
    /// rules, and persist so the next lookup for this title is served from the cache/DB alone. Any
    /// failure here must never escape the tag lookup - callers treat an empty genre list as "no tags"
    /// rather than a rejection.</summary>
    private async Task<List<string>> TryEnrichGenresAsync(AppDbContext db, LibraryCatalogEntry candidateRow, CancellationToken cancellationToken)
    {
        try
        {
            var provider = _registry.FindByName(candidateRow.Provider);
            if (provider is null || !provider.IsEnabled)
                return [];

            var dto = await provider.GetDetailsAsync(candidateRow.ProviderId, cancellationToken).ConfigureAwait(false);
            if (dto is null)
                return [];

            var trackedRow = await db.LibraryCatalogEntries
                .FirstOrDefaultAsync(e => e.Provider == candidateRow.Provider && e.ProviderId == candidateRow.ProviderId, cancellationToken)
                .ConfigureAwait(false);

            if (trackedRow is null)
                return [];

            LibraryCatalogSyncBackgroundService.ApplyDto(trackedRow, dto);
            trackedRow.LastRefreshedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return LibraryCatalogEntry.SplitList(trackedRow.Genres);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Catalog enrichment failed for {Provider}/{ProviderId}.", candidateRow.Provider, candidateRow.ProviderId);
            return [];
        }
    }
}
