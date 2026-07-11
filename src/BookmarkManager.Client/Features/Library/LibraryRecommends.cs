using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Features.Library;

/// <summary>Weighted pick logic for the hero "Recommends" rail — favors high-rated, recently
/// updated, popular catalog rows (like Novelfire's recommends refresh) while still shuffling
/// positions so repeat visits feel fresh without becoming pure random noise.</summary>
public static class LibraryRecommends
{
    private const double BookmarkedWebnovelPenalty = 0.16;
    private const double BookmarkedOtherPenalty = 0.32;
    private const double BookmarkedPickWeight = 0.22;

    public static double Score(LibraryItem item, LibraryBookmarkExclusions? exclusions = null)
    {
        var rating = item.Rating ?? 3.5;
        var recency = item.LastReleaseAt is { } released
            ? Math.Clamp(1.0 - (DateTimeOffset.UtcNow - released).TotalDays / 120.0, 0, 1)
            : 0.25;
        var popularity = item.IsTrending ? 1.0 : 0.45;
        var score = rating * 2.2 + recency * 3.5 + popularity * 2.0;

        if (exclusions?.Contains(item) == true)
        {
            var penalty = item.Type == LibraryMediaType.Webnovel
                ? BookmarkedWebnovelPenalty
                : BookmarkedOtherPenalty;
            score *= penalty;
        }

        return score;
    }

    /// <summary>Builds a hero-sized rail where the head of the score distribution dominates (~75%
    /// of picks) but shuffle jitter still rotates which trendy titles surface.</summary>
    public static List<LibraryItem> BuildRail(
        IReadOnlyList<LibraryItem> pool,
        int take,
        int seed,
        LibraryBookmarkExclusions? exclusions = null)
    {
        if (pool.Count == 0 || take <= 0)
            return [];

        var rng = new Random(seed);
        var ranked = pool
            .Select(item => (Item: item, Score: Score(item, exclusions)))
            .OrderByDescending(x => x.Score)
            .ThenBy(_ => rng.Next())
            .ToList();

        var headSize = Math.Max(1, (int)Math.Ceiling(ranked.Count * 0.35));
        var head = ranked.Take(headSize).Select(x => x.Item).ToList();
        var tail = ranked.Skip(headSize).Select(x => x.Item).ToList();

        var picks = new List<LibraryItem>();
        var headCopy = new List<LibraryItem>(head);
        var tailCopy = new List<LibraryItem>(tail);

        while (picks.Count < take && (headCopy.Count > 0 || tailCopy.Count > 0))
        {
            var useHead = (tailCopy.Count == 0) || (headCopy.Count > 0 && rng.NextDouble() < 0.75);
            var source = useHead ? headCopy : tailCopy;
            var index = WeightedFrontIndex(source, exclusions, rng);
            var candidate = source[index];
            source.RemoveAt(index);

            if (picks.All(p => !SameSeries(p, candidate)))
                picks.Add(candidate);
        }

        return picks;
    }

    private static bool SameSeries(LibraryItem a, LibraryItem b) =>
        string.Equals(a.Provider, b.Provider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.ProviderId, b.ProviderId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Favors low indices (higher prior score) via 1/(i+1)^0.85 weights.</summary>
    private static int WeightedFrontIndex(
        IReadOnlyList<LibraryItem> source,
        LibraryBookmarkExclusions? exclusions,
        Random rng)
    {
        var count = source.Count;
        if (count <= 1)
            return 0;

        var weights = new double[count];
        var total = 0.0;
        for (var i = 0; i < count; i++)
        {
            var weight = 1.0 / Math.Pow(i + 1, 0.85);
            if (exclusions?.Contains(source[i]) == true)
                weight *= BookmarkedPickWeight;

            weights[i] = weight;
            total += weight;
        }

        if (total <= 0)
            return rng.Next(count);

        var roll = rng.NextDouble() * total;
        var acc = 0.0;
        for (var i = 0; i < count; i++)
        {
            acc += weights[i];
            if (roll <= acc)
                return i;
        }

        return count - 1;
    }
}
