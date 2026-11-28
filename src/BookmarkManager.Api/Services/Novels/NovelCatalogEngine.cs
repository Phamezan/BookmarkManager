using System.Globalization;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Novels;

/// <summary>
/// Cached in-memory projection of a novel <see cref="LibraryCatalogEntry"/> with derived ranking,
/// chapter count, normalized titles, and direct-readability metadata.
/// </summary>
public sealed record NovelCatalogRow
{
    public Guid Id { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<string> AlternateTitles { get; init; } = [];
    public IReadOnlyList<string> Authors { get; init; } = [];
    public LibraryMediaType MediaType { get; init; }
    public string? CoverUrl { get; init; }
    public string? Synopsis { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = [];
    public HashSet<string> GenreSet { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public double? Rating { get; init; }
    public string? Status { get; init; }
    public string? LatestChapter { get; init; }
    public string? LatestVolume { get; init; }
    public DateTimeOffset? LastReleaseAt { get; init; }
    public string SourceUrl { get; init; } = string.Empty;
    public int? PopularityRank { get; init; }
    public double PopularityScore { get; init; } = 1.0;
    public int? Chapters { get; init; }
    public bool IsNovelfireBookUrl { get; init; }
    public string NormalizedTitle { get; init; } = string.Empty;
    public IReadOnlyList<string> NormalizedAlternateTitles { get; init; } = [];

    public bool IsReadable(bool novelfireEnabled) => novelfireEnabled && IsNovelfireBookUrl;
}

/// <summary>Pure catalog normalization, chapter parsing, filtering, and sorting helpers.</summary>
public static partial class NovelCatalogEngine
{
    /// <summary>
    /// Computes provider-normalized popularity scores (<c>PopularityRank / maxRankOfThatProvider</c>;
    /// null or non-positive rank becomes <c>1.0</c>) and builds in-memory <see cref="NovelCatalogRow"/> records.
    /// </summary>
    public static List<NovelCatalogRow> BuildRows(IReadOnlyList<LibraryCatalogEntry> entries)
    {
        var maxRankByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.PopularityRank is > 0 and var rank)
            {
                if (!maxRankByProvider.TryGetValue(entry.Provider, out var current) || rank > current)
                {
                    maxRankByProvider[entry.Provider] = rank;
                }
            }
        }

        var rows = new List<NovelCatalogRow>(entries.Count);
        foreach (var entry in entries)
        {
            var alts = LibraryCatalogEntry.SplitList(entry.AlternateTitles);
            var genres = LibraryCatalogEntry.SplitList(entry.Genres);
            var score = ComputePopularityScore(entry.PopularityRank,
                maxRankByProvider.TryGetValue(entry.Provider, out var maxRank) ? maxRank : 0);

            rows.Add(new NovelCatalogRow
            {
                Id = entry.Id,
                Provider = entry.Provider,
                ProviderId = entry.ProviderId,
                Title = entry.Title,
                AlternateTitles = alts,
                Authors = LibraryCatalogEntry.SplitList(entry.Authors),
                MediaType = entry.MediaType,
                CoverUrl = entry.CoverImageUrl,
                // Catalog synopses can arrive still HTML-encoded (Novelfire meta description: &amp;ldquo;).
                Synopsis = BookmarkManager.Api.Services.Explore.ExploreHtmlText.Strip(entry.Synopsis),
                Genres = genres,
                GenreSet = new HashSet<string>(genres, StringComparer.OrdinalIgnoreCase),
                Rating = entry.Rating,
                Status = entry.Status,
                LatestChapter = entry.LatestChapter,
                LatestVolume = entry.LatestVolume,
                LastReleaseAt = entry.LastReleaseAt,
                SourceUrl = entry.SourceUrl,
                PopularityRank = entry.PopularityRank,
                PopularityScore = score,
                Chapters = ParseChapters(entry.LatestChapter),
                IsNovelfireBookUrl = IsNovelfireBookUrl(entry.Provider, entry.SourceUrl),
                NormalizedTitle = MediaTitleNormalizer.NormalizeForSearch(entry.Title),
                NormalizedAlternateTitles = alts
                    .Select(MediaTitleNormalizer.NormalizeForSearch)
                    .Where(t => t.Length > 0)
                    .ToList()
            });
        }

        return rows;
    }

    public static double ComputePopularityScore(int? popularityRank, int maxRankOfProvider)
    {
        if (popularityRank is not (> 0 and var rank) || maxRankOfProvider <= 0)
        {
            return 1.0;
        }

        return Math.Clamp((double)rank / maxRankOfProvider, 0.0, 1.0);
    }

    public static int? ParseChapters(string? latestChapter)
    {
        if (string.IsNullOrWhiteSpace(latestChapter))
        {
            return null;
        }

        var parsed = LibraryLatestChapterParser.Parse(latestChapter);
        if (parsed is >= 0 and var value)
        {
            return (int)Math.Floor(value);
        }

        var trailing = TrailingChaptersRegex().Match(latestChapter.Trim());
        if (trailing.Success
            && double.TryParse(trailing.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var trailingValue)
            && trailingValue >= 0)
        {
            return (int)Math.Floor(trailingValue);
        }

        return null;
    }

    public static bool IsNovelfireBookUrl(string? provider, string? sourceUrl)
    {
        if (!string.Equals(provider, "Novelfire", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(sourceUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https"
            && string.Equals(uri.Host, "novelfire.net", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/book/", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Length > "/book/".Length;
    }

    public static bool IsInLibrary(NovelCatalogRow row, ISet<string> normalizedLibraryTitles)
    {
        if (normalizedLibraryTitles.Count == 0)
        {
            return false;
        }

        if (row.NormalizedTitle.Length > 0 && normalizedLibraryTitles.Contains(row.NormalizedTitle))
        {
            return true;
        }

        foreach (var alt in row.NormalizedAlternateTitles)
        {
            if (normalizedLibraryTitles.Contains(alt))
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<NovelCatalogRow> Filter(
        IEnumerable<NovelCatalogRow> rows,
        NovelCatalogFilter filter,
        bool novelfireEnabled,
        ISet<string> normalizedLibraryTitles,
        IReadOnlyDictionary<Guid, string> listStates)
    {
        foreach (var row in rows)
        {
            if (filter.Readable && !row.IsReadable(novelfireEnabled))
            {
                continue;
            }

            if (filter.HideDismissed
                && listStates.TryGetValue(row.Id, out var state)
                && string.Equals(state, NovelListEntry.Dismissed, StringComparison.Ordinal))
            {
                continue;
            }

            if (filter.HideLibrary && IsInLibrary(row, normalizedLibraryTitles))
            {
                continue;
            }

            if (filter.Type is { } wantedType && row.MediaType != wantedType)
            {
                continue;
            }

            if (!NovelCatalogFilter.MatchesStatuses(row.Status, filter.Statuses))
            {
                continue;
            }

            if (filter.Query is { Length: > 0 } term && !MatchesQuery(row, term))
            {
                continue;
            }

            if (filter.Genres.Count > 0 && !HasAllGenres(row, filter.Genres))
            {
                continue;
            }

            if (filter.ExcludeGenres.Count > 0 && HasAnyGenre(row, filter.ExcludeGenres))
            {
                continue;
            }

            if (filter.MinRating is { } minRating && (row.Rating is null || row.Rating < minRating))
            {
                continue;
            }

            if (filter.MinChapters is { } minChapters && (row.Chapters is null || row.Chapters < minChapters))
            {
                continue;
            }

            if (filter.MaxChapters is { } maxChapters && (row.Chapters is null || row.Chapters > maxChapters))
            {
                continue;
            }

            yield return row;
        }
    }

    public static IEnumerable<NovelCatalogRow> Sort(IEnumerable<NovelCatalogRow> rows, string sort) => sort switch
    {
        NovelCatalogFilter.SortRating => rows
            .OrderBy(r => r.Rating is null ? 1 : 0)
            .ThenByDescending(r => r.Rating)
            .ThenBy(r => r.PopularityScore)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase),
        NovelCatalogFilter.SortUpdated => rows
            .OrderBy(r => r.LastReleaseAt is null ? 1 : 0)
            .ThenByDescending(r => r.LastReleaseAt)
            .ThenBy(r => r.PopularityScore)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase),
        NovelCatalogFilter.SortChapters => rows
            .OrderBy(r => r.Chapters is null ? 1 : 0)
            .ThenByDescending(r => r.Chapters)
            .ThenBy(r => r.PopularityScore)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase),
        NovelCatalogFilter.SortTitle => rows
            .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase),
        _ => rows
            .OrderBy(r => r.PopularityScore)
            .ThenBy(r => r.PopularityRank is null ? 1 : 0)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
    };

    public static NovelCatalogItemDto ToDto(
        NovelCatalogRow row,
        bool novelfireEnabled,
        ISet<string> normalizedLibraryTitles,
        IReadOnlyDictionary<Guid, string> listStates,
        IReadOnlyList<string>? becauseOf = null) => new()
    {
        Id = row.Id,
        Provider = row.Provider,
        Title = row.Title,
        AlternateTitles = [.. row.AlternateTitles],
        Authors = [.. row.Authors],
        MediaType = row.MediaType.ToString(),
        CoverUrl = row.CoverUrl,
        Synopsis = row.Synopsis,
        Genres = [.. row.Genres],
        Rating = row.Rating,
        Status = row.Status,
        LatestChapter = row.LatestChapter,
        LatestVolume = row.LatestVolume,
        LastReleaseAt = row.LastReleaseAt,
        SourceUrl = row.SourceUrl,
        PopularityRank = row.PopularityRank,
        InLibrary = IsInLibrary(row, normalizedLibraryTitles),
        Readable = row.IsReadable(novelfireEnabled),
        Chapters = row.Chapters,
        ListState = listStates.TryGetValue(row.Id, out var state) ? state : null,
        BecauseOf = becauseOf is null ? [] : [.. becauseOf]
    };

    private static bool MatchesQuery(NovelCatalogRow row, string term)
    {
        if (row.Title.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var alt in row.AlternateTitles)
        {
            if (alt.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAllGenres(NovelCatalogRow row, IReadOnlyList<string> wanted)
    {
        foreach (var genre in wanted)
        {
            if (!row.GenreSet.Contains(genre))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasAnyGenre(NovelCatalogRow row, IReadOnlyList<string> excluded)
    {
        foreach (var genre in excluded)
        {
            if (row.GenreSet.Contains(genre))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(?:chapters?|ch\.?)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingChaptersRegex();
}
