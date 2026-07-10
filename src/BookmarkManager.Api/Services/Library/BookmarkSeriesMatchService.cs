using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Library;

/// <summary>One bookmark matched to a catalog series, with the reading progress extracted from it.</summary>
public sealed record BookmarkSeriesMatch(
    string Provider,
    string ProviderId,
    double? CurrentChapter,
    string? RawProgressText,
    double Confidence,
    Guid BookmarkId,
    string? CoverImageUrl);

/// <summary>
/// Matches the user's bookmark tree against <see cref="LibraryCatalogEntry"/> rows so Library cards
/// can show reading-progress badges. Read-only over bookmarks (never writes them) - see the Library
/// scope boundary in CLAUDE.md. Caches the built match set and rebuilds it lazily: the bookmark side
/// is detected automatically (a cheap fingerprint over non-deleted bookmark count + summed
/// <see cref="BookmarkNode.Version"/> catches create/update/move/delete/restore), while catalog-side
/// changes require an explicit <see cref="InvalidateCatalog"/> call from
/// <see cref="LibraryCatalogSyncBackgroundService"/> after a sync completes.
/// </summary>
public sealed class BookmarkSeriesMatchService(
    IServiceScopeFactory scopeFactory,
    ILogger<BookmarkSeriesMatchService> logger)
{
    private const double ConfidenceThreshold = 0.8;

    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private IReadOnlyList<BookmarkSeriesMatch> _cachedMatches = [];
    private (int Count, long VersionSum)? _cachedFingerprint;
    private volatile bool _catalogDirty = true;

    /// <summary>Marks the cache stale after a catalog sync writes new/updated entries.</summary>
    public void InvalidateCatalog() => _catalogDirty = true;

    public async Task<IReadOnlyList<BookmarkSeriesMatch>> GetMatchesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var fingerprint = await ComputeFingerprintAsync(db, cancellationToken).ConfigureAwait(false);
        if (!_catalogDirty && _cachedFingerprint == fingerprint)
            return _cachedMatches;

        await _rebuildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            fingerprint = await ComputeFingerprintAsync(db, cancellationToken).ConfigureAwait(false);
            if (!_catalogDirty && _cachedFingerprint == fingerprint)
                return _cachedMatches;

            _cachedMatches = await BuildMatchesAsync(db, cancellationToken).ConfigureAwait(false);
            _cachedFingerprint = fingerprint;
            _catalogDirty = false;
            logger.LogInformation("Rebuilt bookmark/series match set: {Count} matches.", _cachedMatches.Count);
            return _cachedMatches;
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private static async Task<(int Count, long VersionSum)> ComputeFingerprintAsync(AppDbContext db, CancellationToken ct)
    {
        var versions = await db.BookmarkNodes
            .Where(n => !n.IsDeleted && n.Type == NodeType.Bookmark)
            .Select(n => n.Version)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (versions.Count, versions.Sum(v => (long)v));
    }

    // Distinctive-token length floor for the inverted index: below this, generic connector
    // words ("the", "of", "no", ...) would map to a large slice of the catalog and defeat the
    // point of pruning. Titles under this length still get compared via any longer token they share.
    private const int MinIndexTokenLength = 4;

    private readonly record struct CatalogCandidate(string Provider, string ProviderId, string? CoverImageUrl, HashSet<string> Tokens);

    private static async Task<IReadOnlyList<BookmarkSeriesMatch>> BuildMatchesAsync(AppDbContext db, CancellationToken ct)
    {
        var bookmarks = await db.BookmarkNodes
            .Where(n => !n.IsDeleted && n.Type == NodeType.Bookmark && n.Url != null)
            .Select(n => new { n.Id, n.Title, n.Url })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var catalogEntries = await db.LibraryCatalogEntries
            .Select(e => new { e.Provider, e.ProviderId, e.Title, e.AlternateTitles, e.CoverImageUrl })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Comparing every bookmark against every catalog entry is O(bookmarks * catalog) with
        // regex-heavy normalization on each pairing - at real-world catalog sizes (tens of
        // thousands of rows) that's tens of millions of comparisons and minutes of wall time.
        // Tokenize each side once, then build an inverted index (distinctive token -> candidate
        // titles containing it) so a bookmark only gets scored against catalog entries it could
        // plausibly match, not the entire catalog.
        var candidates = new List<CatalogCandidate>();
        var tokenIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var entry in catalogEntries)
        {
            var titles = new List<string>(1 + LibraryCatalogEntry.SplitList(entry.AlternateTitles).Count) { entry.Title };
            titles.AddRange(LibraryCatalogEntry.SplitList(entry.AlternateTitles));

            foreach (var title in titles)
            {
                var tokens = MediaTitleNormalizer.NormalizeForSearch(title)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.Ordinal);
                if (tokens.Count == 0)
                    continue;

                var candidateIndex = candidates.Count;
                candidates.Add(new CatalogCandidate(entry.Provider, entry.ProviderId, entry.CoverImageUrl, tokens));

                foreach (var token in tokens.Where(t => t.Length >= MinIndexTokenLength))
                {
                    if (!tokenIndex.TryGetValue(token, out var list))
                        tokenIndex[token] = list = new List<int>();
                    list.Add(candidateIndex);
                }
            }
        }

        // Highest chapter wins when multiple bookmarks match the same series.
        var bestPerSeries = new Dictionary<string, BookmarkSeriesMatch>(StringComparer.OrdinalIgnoreCase);

        foreach (var bookmark in bookmarks)
        {
            var extraction = BookmarkProgressExtractor.Extract(bookmark.Title, bookmark.Url);
            if (extraction.SeriesName.Length < 2)
                continue;

            var queryTokens = MediaTitleNormalizer.NormalizeForSearch(extraction.SeriesName)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
            if (queryTokens.Count == 0)
                continue;

            var candidateIndexes = new HashSet<int>();
            foreach (var token in queryTokens.Where(t => t.Length >= MinIndexTokenLength))
            {
                if (tokenIndex.TryGetValue(token, out var list))
                    candidateIndexes.UnionWith(list);
            }

            BookmarkSeriesMatch? bestForBookmark = null;
            var bestScore = 0.0;

            foreach (var candidateIndex in candidateIndexes)
            {
                var candidate = candidates[candidateIndex];
                var score = MediaTitleNormalizer.ScoreTokenSets(queryTokens, candidate.Tokens);
                if (score < ConfidenceThreshold || score <= bestScore)
                    continue;

                bestScore = score;
                bestForBookmark = new BookmarkSeriesMatch(
                    candidate.Provider, candidate.ProviderId, extraction.CurrentChapter, extraction.RawProgressText, score, bookmark.Id, candidate.CoverImageUrl);
            }

            if (bestForBookmark is null)
                continue;

            var key = $"{bestForBookmark.Provider}:{bestForBookmark.ProviderId}";
            if (!bestPerSeries.TryGetValue(key, out var existing) ||
                (bestForBookmark.CurrentChapter ?? -1) > (existing.CurrentChapter ?? -1))
            {
                bestPerSeries[key] = bestForBookmark;
            }
        }

        return bestPerSeries.Values.ToList();
    }
}
