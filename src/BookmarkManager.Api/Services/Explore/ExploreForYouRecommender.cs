using System.Text.Json;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Explore;

/// <summary>One AniList recommendation of a library series: community rating plus the recommended media.</summary>
public sealed record ForYouRecommendation(int Rating, JsonElement Media);

/// <summary>A library series and what AniList users recommend after it.</summary>
public sealed record ForYouSource(string LibraryTitle, IReadOnlyList<ForYouRecommendation> Recommendations);

/// <summary>Builds the "For you" ranking from AniList recommendations of the user's library series.</summary>
public sealed class ExploreForYouRecommender
{
    public const int PageSize = 30;
    private const int MaxLibrarySeries = 200;
    private const int MaxBecauseOf = 3;
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    private readonly ExploreAniListClient _client;

    public ExploreForYouRecommender(ExploreAniListClient client)
    {
        _client = client;
    }

    /// <summary>Fetches the recommendations of <paramref name="libraryAnilistIds"/> in batches of 50 (cached 12 h per batch).</summary>
    public async Task<IReadOnlyList<ForYouSource>> LoadSourcesAsync(
        IReadOnlyCollection<int> libraryAnilistIds, CancellationToken ct)
    {
        // Callers pass ids most-read first; sort the kept ids so batch cache keys stay stable.
        var ids = libraryAnilistIds.Distinct().Take(MaxLibrarySeries).Order().ToList();
        var sources = new List<ForYouSource>();

        foreach (var batch in ids.Chunk(ExploreQueries.ForYouBatchSize))
        {
            var variables = new Dictionary<string, object?>
            {
                ["ids"] = batch,
                ["perPage"] = ExploreQueries.ForYouBatchSize
            };
            var data = await _client.QueryAsync("foryou", ExploreQueries.ForYouBatch, variables, Ttl, ct)
                .ConfigureAwait(false);
            sources.AddRange(ParseSources(data));
        }

        return sources;
    }

    internal static List<ForYouSource> ParseSources(JsonElement data)
    {
        var media = ExploreMediaParser.Obj(ExploreMediaParser.Obj(data, "Page"), "media");
        var sources = new List<ForYouSource>();
        if (media.ValueKind != JsonValueKind.Array)
        {
            return sources;
        }

        foreach (var entry in media.EnumerateArray())
        {
            var title = ExploreMediaParser.Str(ExploreMediaParser.Obj(entry, "title"), "english")
                ?? ExploreMediaParser.Str(ExploreMediaParser.Obj(entry, "title"), "romaji");
            var nodes = ExploreMediaParser.Obj(ExploreMediaParser.Obj(entry, "recommendations"), "nodes");
            if (title is null || nodes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var recs = nodes.EnumerateArray()
                .Select(n => new ForYouRecommendation(
                    ExploreMediaParser.Int(n, "rating") ?? 0, ExploreMediaParser.Obj(n, "mediaRecommendation")))
                .Where(r => r.Media.ValueKind == JsonValueKind.Object)
                .ToList();
            sources.Add(new ForYouSource(title, recs));
        }

        return sources;
    }

    /// <summary>
    /// Score of a recommended series is the sum of max(rating, 1) over the library series that
    /// recommend it. Excluded ids (library, dismissed) and adult media are dropped; best first, ties by id.
    /// <c>BecauseOf</c> holds up to three recommending library titles, highest rating first.
    /// </summary>
    public static List<ExploreItemDto> Rank(IEnumerable<ForYouSource> sources, ISet<int> excludedIds)
    {
        var scores = new Dictionary<int, int>();
        var reasons = new Dictionary<int, List<(string Title, int Rating)>>();
        var items = new Dictionary<int, ExploreItemDto>();

        foreach (var source in sources)
        {
            foreach (var rec in source.Recommendations)
            {
                if (ExploreMediaParser.Bool(rec.Media, "isAdult")
                    || ExploreMediaParser.ParseItem(rec.Media) is not { } item
                    || excludedIds.Contains(item.AnilistId))
                {
                    continue;
                }

                var weight = Math.Max(rec.Rating, 1);
                items.TryAdd(item.AnilistId, item);
                scores[item.AnilistId] = scores.GetValueOrDefault(item.AnilistId) + weight;
                if (!reasons.TryGetValue(item.AnilistId, out var list))
                {
                    reasons[item.AnilistId] = list = [];
                }

                list.Add((source.LibraryTitle, weight));
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Select(kv =>
            {
                var item = items[kv.Key];
                item.BecauseOf = reasons[kv.Key]
                    .OrderByDescending(r => r.Rating)
                    .Select(r => r.Title)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxBecauseOf)
                    .ToList();
                return item;
            })
            .ToList();
    }
}
