using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Explore;

/// <summary>Finds where a series can be read: searches the configured Suwayomi sources by its titles.</summary>
public sealed class ExploreSourceFinder
{
    private const int MaxSynonymQueries = 2;

    private readonly ExploreAniListClient _anilist;
    private readonly SuwayomiSourceSearcher _searcher;

    public ExploreSourceFinder(ExploreAniListClient anilist, SuwayomiSourceSearcher searcher)
    {
        _anilist = anilist;
        _searcher = searcher;
    }

    /// <summary>Hits for an AniList series; null when AniList does not know the id.</summary>
    public async Task<List<ExploreSourceHitDto>?> FindAsync(int anilistId, CancellationToken ct)
    {
        var detail = await _anilist.GetDetailAsync(anilistId, ct).ConfigureAwait(false);
        return detail is null ? null : await FindAsync(detail, ct).ConfigureAwait(false);
    }

    public async Task<List<ExploreSourceHitDto>> FindAsync(ExploreItemDto series, CancellationToken ct)
    {
        var queries = BuildQueries(series);
        foreach (var query in queries)
        {
            var hits = await _searcher.SearchAsync(query, null, ct).ConfigureAwait(false);
            if (hits.Count > 0)
            {
                return Order(hits, KnownTitleKeys(series));
            }
        }

        return [];
    }

    /// <summary>English, romaji, then the first two synonyms; blanks and normalized duplicates removed.</summary>
    public static List<string> BuildQueries(ExploreItemDto series)
    {
        var candidates = new List<string?> { series.Title, series.RomajiTitle };
        candidates.AddRange(series.Synonyms.Take(MaxSynonymQueries));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Where(c => seen.Add(MediaTitleNormalizer.NormalizeForSearch(c)))
            .ToList();
    }

    /// <summary>Exact title matches first; the sort is stable so source order is kept within each group.</summary>
    public static List<ExploreSourceHitDto> Order(IEnumerable<SuwayomiSearchResultDto> hits, HashSet<string> knownKeys)
        => hits
            .Select(h => new ExploreSourceHitDto
            {
                MangaId = h.MangaId,
                Title = h.Title,
                SourceName = h.SourceName,
                InLibrary = h.InLibrary,
                IsExactMatch = knownKeys.Contains(MediaTitleNormalizer.NormalizeForSearch(h.Title))
            })
            .OrderByDescending(h => h.IsExactMatch)
            .ToList();

    private static HashSet<string> KnownTitleKeys(ExploreItemDto series)
        => new[] { series.Title, series.RomajiTitle, series.NativeTitle }
            .Concat(series.Synonyms)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => MediaTitleNormalizer.NormalizeForSearch(t!))
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
}
