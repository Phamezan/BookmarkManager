using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>
/// Merges Discover cards that are the same series listed under different titles. Two items merge only
/// when the metadata maps both of their title keys to the same non-null AniList id; an item with no
/// AniList id is always kept as its own card. The merge is pure and database-free so the response
/// rules are unit-testable.
/// </summary>
public static class DiscoverSeriesMerger
{
    /// <summary>
    /// Collapses <paramref name="items"/> sharing an AniList id into one card. The result is ordered
    /// by each card's primary item's newest chapter, newest first — the same recency key the endpoint
    /// uses today.
    /// </summary>
    public static List<DiscoverItemDto> Merge(
        IReadOnlyList<DiscoverItemDto> items,
        IReadOnlyDictionary<string, int> anilistIdByTitleKey)
    {
        var groups = new List<List<DiscoverItemDto>>();
        var byAnilistId = new Dictionary<int, List<DiscoverItemDto>>();

        foreach (var item in items)
        {
            if (anilistIdByTitleKey.TryGetValue(item.TitleKey, out var anilistId) && anilistId > 0)
            {
                if (!byAnilistId.TryGetValue(anilistId, out var group))
                {
                    group = [];
                    byAnilistId[anilistId] = group;
                    groups.Add(group);
                }

                group.Add(item);
            }
            else
            {
                groups.Add([item]);
            }
        }

        return groups
            .Select(group => group.Count == 1 ? group[0] : Combine(group))
            .OrderByDescending(LatestChapterAt)
            .ToList();
    }

    private static DiscoverItemDto Combine(IReadOnlyList<DiscoverItemDto> group)
    {
        var primary = group.Aggregate(SelectPrimary);

        var sources = new List<string>();
        AddSources(sources, primary);
        foreach (var item in group)
        {
            if (!ReferenceEquals(item, primary))
            {
                AddSources(sources, item);
            }
        }

        var chapters = group
            .SelectMany(item => item.Chapters)
            .GroupBy(chapter => chapter.Number)
            .Select(duplicates => duplicates.OrderBy(chapter => chapter.UploadedAt).First())
            .OrderByDescending(chapter => chapter.Number)
            .Take(3)
            .ToList();

        var bestProgress = group
            .Where(item => item.Progress is not null)
            .OrderByDescending(item => item.Progress!.Read)
            .Select(item => item.Progress!)
            .FirstOrDefault();

        var type = primary.Type != "Unknown"
            ? primary.Type
            : group.Select(item => item.Type).FirstOrDefault(value => value != "Unknown") ?? "Unknown";

        return new DiscoverItemDto
        {
            TitleKey = primary.TitleKey,
            Title = primary.Title,
            Type = type,
            CoverMangaId = primary.CoverMangaId,
            SeriesMangaId = primary.SeriesMangaId,
            Sources = sources,
            Chapters = chapters,
            Progress = bestProgress is null
                ? null
                : new DiscoverProgressDto { Read = bestProgress.Read, Latest = bestProgress.Latest }
        };
    }

    private static void AddSources(List<string> sources, DiscoverItemDto item)
    {
        foreach (var source in item.Sources)
        {
            if (!sources.Contains(source, StringComparer.Ordinal))
            {
                sources.Add(source);
            }
        }
    }

    private static DiscoverItemDto SelectPrimary(DiscoverItemDto a, DiscoverItemDto b)
    {
        var aLatest = LatestChapterAt(a);
        var bLatest = LatestChapterAt(b);
        if (aLatest != bLatest)
        {
            return aLatest > bLatest ? a : b;
        }

        if ((a.Progress is not null) != (b.Progress is not null))
        {
            return a.Progress is not null ? a : b;
        }

        if (a.Sources.Count != b.Sources.Count)
        {
            return a.Sources.Count > b.Sources.Count ? a : b;
        }

        return a;
    }

    private static DateTime LatestChapterAt(DiscoverItemDto item)
        => item.Chapters.Count == 0 ? DateTime.MinValue : item.Chapters.Max(chapter => chapter.UploadedAt);
}
