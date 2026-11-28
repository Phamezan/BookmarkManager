using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Api.Services.Library;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.Novels;

public sealed record ScoredNovelRecommendation(NovelCatalogRow Row, double Score, List<string> BecauseOf);

/// <summary>
/// Builds personalized novel recommendations from novel library entries and matched novel bookmarks.
/// Uses <see cref="IVectorSearchService"/> over seed embeddings when available, falling back to a
/// seed genre-frequency profile when no embeddings or vector matches exist.
/// </summary>
public sealed class NovelForYouScorer
{
    public const int MaxSeeds = 200;
    public const int VectorK = 25;
    public const float VectorFloor = 0.55f;
    public const int MaxBecauseOf = 3;

    private readonly AppDbContext _db;
    private readonly BookmarkSeriesMatchService _bookmarkMatches;
    private readonly IVectorSearchService _vectorSearch;

    public NovelForYouScorer(
        AppDbContext db,
        BookmarkSeriesMatchService bookmarkMatches,
        IVectorSearchService vectorSearch)
    {
        _db = db;
        _bookmarkMatches = bookmarkMatches;
        _vectorSearch = vectorSearch;
    }

    public async Task<List<ScoredNovelRecommendation>> RankAsync(
        IReadOnlyList<NovelCatalogRow> allRows,
        bool readableOnly,
        bool novelfireEnabled,
        ISet<string> normalizedLibraryTitles,
        IReadOnlyDictionary<Guid, string> listStates,
        CancellationToken ct)
    {
        if (allRows.Count == 0)
        {
            return [];
        }

        var seeds = await ResolveSeedsAsync(allRows, normalizedLibraryTitles, ct).ConfigureAwait(false);
        if (seeds.Count == 0)
        {
            return [];
        }

        var seedIds = seeds.Select(s => s.Id).ToHashSet();
        var byId = allRows.ToDictionary(r => r.Id);

        bool IsEligible(NovelCatalogRow row) =>
            !seedIds.Contains(row.Id)
            && (!readableOnly || row.IsReadable(novelfireEnabled))
            && !NovelCatalogEngine.IsInLibrary(row, normalizedLibraryTitles)
            && !(listStates.TryGetValue(row.Id, out var st)
                 && string.Equals(st, NovelListEntry.Dismissed, StringComparison.Ordinal));

        var vectorResults = await ScoreByEmbeddingsAsync(seeds, byId, IsEligible, ct).ConfigureAwait(false);
        if (vectorResults.Count > 0)
        {
            return vectorResults;
        }

        var candidates = allRows.Where(IsEligible);
        return ScoreByGenreProfile(seeds, candidates);
    }

    private async Task<List<NovelCatalogRow>> ResolveSeedsAsync(
        IReadOnlyList<NovelCatalogRow> allRows,
        ISet<string> normalizedLibraryTitles,
        CancellationToken ct)
    {
        var seedMap = new Dictionary<Guid, NovelCatalogRow>();

        // (a) NovelLibraryEntries title matches (normalized title or alt title)
        if (normalizedLibraryTitles.Count > 0)
        {
            foreach (var row in allRows)
            {
                if (NovelCatalogEngine.IsInLibrary(row, normalizedLibraryTitles))
                {
                    seedMap.TryAdd(row.Id, row);
                    if (seedMap.Count >= MaxSeeds)
                    {
                        return [.. seedMap.Values];
                    }
                }
            }
        }

        // (b) User's bookmarks matched to catalog novel rows
        var matches = await _bookmarkMatches.GetMatchesAsync(ct).ConfigureAwait(false);
        if (matches.Count > 0)
        {
            var byProviderKey = new Dictionary<string, NovelCatalogRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in allRows)
            {
                byProviderKey.TryAdd($"{row.Provider}:{row.ProviderId}", row);
            }

            foreach (var match in matches)
            {
                if (seedMap.Count >= MaxSeeds)
                {
                    break;
                }

                if (byProviderKey.TryGetValue($"{match.Provider}:{match.ProviderId}", out var row))
                {
                    seedMap.TryAdd(row.Id, row);
                }
            }
        }

        return [.. seedMap.Values];
    }

    private async Task<List<ScoredNovelRecommendation>> ScoreByEmbeddingsAsync(
        IReadOnlyList<NovelCatalogRow> seeds,
        IReadOnlyDictionary<Guid, NovelCatalogRow> byId,
        Func<NovelCatalogRow, bool> isEligible,
        CancellationToken ct)
    {
        var seedIdList = seeds.Select(s => s.Id).ToList();
        var embeddedRows = await _db.LibraryCatalogEntries
            .AsNoTracking()
            .Where(e => seedIdList.Contains(e.Id) && e.Embedding != null)
            .Select(e => new { e.Id, e.Embedding })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (embeddedRows.Count == 0)
        {
            return [];
        }

        var seedTitleById = seeds.ToDictionary(s => s.Id, s => s.Title);
        var scores = new Dictionary<Guid, double>();
        var contributors = new Dictionary<Guid, List<(string Title, double Score)>>();

        foreach (var embedded in embeddedRows)
        {
            var vector = new LibraryCatalogEntry { Embedding = embedded.Embedding }.GetEmbeddingVector();
            if (vector is not { Length: > 0 })
            {
                continue;
            }

            var seedTitle = seedTitleById[embedded.Id];
            var neighbors = await _vectorSearch.SearchAsync(vector, VectorK, VectorFloor, ct).ConfigureAwait(false);
            foreach (var (candidateId, similarity) in neighbors)
            {
                if (!byId.TryGetValue(candidateId, out var candidate) || !isEligible(candidate))
                {
                    continue;
                }

                scores[candidateId] = scores.GetValueOrDefault(candidateId) + similarity;
                if (!contributors.TryGetValue(candidateId, out var list))
                {
                    contributors[candidateId] = list = [];
                }

                list.Add((seedTitle, similarity));
            }
        }

        if (scores.Count == 0)
        {
            return [];
        }

        return scores
            .Select(kv =>
            {
                var row = byId[kv.Key];
                var because = contributors[kv.Key]
                    .OrderByDescending(c => c.Score)
                    .Select(c => c.Title)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxBecauseOf)
                    .ToList();
                return new ScoredNovelRecommendation(row, kv.Value, because);
            })
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Row.PopularityScore)
            .ThenBy(r => r.Row.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Fallback scorer when no seed has an embedding or vector search yields no novel candidates.
    /// Weights genres by frequency across seeds and scores each candidate by summed genre weights
    /// normalized by the candidate's genre count, tie-breaking by <see cref="NovelCatalogRow.PopularityScore"/>.
    /// </summary>
    public static List<ScoredNovelRecommendation> ScoreByGenreProfile(
        IReadOnlyList<NovelCatalogRow> seeds,
        IEnumerable<NovelCatalogRow> candidates)
    {
        var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var displayName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var seed in seeds)
        {
            foreach (var genre in seed.GenreSet)
            {
                weights[genre] = weights.GetValueOrDefault(genre) + 1;
                displayName.TryAdd(genre, genre);
            }
        }

        if (weights.Count == 0)
        {
            return [];
        }

        var scored = new List<ScoredNovelRecommendation>();
        foreach (var candidate in candidates)
        {
            if (candidate.Genres.Count == 0)
            {
                continue;
            }

            var sum = 0;
            var shared = new List<(string Name, int Weight)>();
            foreach (var genre in candidate.GenreSet)
            {
                if (weights.TryGetValue(genre, out var w))
                {
                    sum += w;
                    shared.Add((displayName[genre], w));
                }
            }

            if (sum <= 0)
            {
                continue;
            }

            var normalized = (double)sum / candidate.GenreSet.Count;
            var because = shared
                .OrderByDescending(s => s.Weight)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxBecauseOf)
                .Select(s => $"Genre: {s.Name}")
                .ToList();

            scored.Add(new ScoredNovelRecommendation(candidate, normalized, because));
        }

        return scored
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Row.PopularityScore)
            .ThenBy(r => r.Row.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
