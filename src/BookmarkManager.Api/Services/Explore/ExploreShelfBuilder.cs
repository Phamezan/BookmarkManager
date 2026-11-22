using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Explore;

/// <summary>Builds the Home shelves and the "For you" ranking.</summary>
public sealed class ExploreShelfBuilder
{
    public const string ForYouBrowseQuery = "view=for-you";

    private readonly ExploreAniListClient _client;
    private readonly ExploreForYouRecommender _recommender;

    public ExploreShelfBuilder(ExploreAniListClient client, ExploreForYouRecommender recommender)
    {
        _client = client;
        _recommender = recommender;
    }

    private sealed record ShelfDefinition(string Key, string Title, string Subtitle, string BrowseQuery, ExploreFilter Filter);

    /// <summary>
    /// Ranked recommendations from the library, minus library series (by id or title) and dismissed
    /// ones. Empty when the library has no AniList-matched series.
    /// </summary>
    public async Task<List<ExploreItemDto>> RankForYouAsync(ExploreUserState state, CancellationToken ct)
    {
        if (state.LibraryByAnilistId.Count == 0)
        {
            return [];
        }

        var excluded = state.LibraryByAnilistId.Keys
            .Concat(state.ListStates.Where(kv => kv.Value == ExploreListEntry.Dismissed).Select(kv => kv.Key))
            .ToHashSet();

        var sources = await _recommender.LoadSourcesAsync(
                state.LibraryByAnilistId.OrderByDescending(kv => kv.Value.Read).Select(kv => kv.Key).ToList(), ct)
            .ConfigureAwait(false);
        return ExploreForYouRecommender.Rank(sources, excluded)
            .Where(item => !ExploreLibraryEnricher.IsInLibrary(item, state))
            .ToList();
    }

    /// <summary>The shelves in display order; AniList requests run one after another (shared rate limiter).</summary>
    public async Task<List<ExploreShelfDto>> BuildAsync(ExploreUserState state, CancellationToken ct)
    {
        var shelves = new List<ExploreShelfDto>();

        var forYou = (await RankForYouAsync(state, ct).ConfigureAwait(false)).Take(ExploreQueries.ShelfPerPage).ToList();
        if (forYou.Count > 0)
        {
            ExploreLibraryEnricher.Apply(forYou, state);
            shelves.Add(new ExploreShelfDto
            {
                Key = "for-you",
                Title = "For you",
                Subtitle = "Recommended from the series in your library",
                BrowseQuery = ForYouBrowseQuery,
                Items = forYou
            });
        }

        foreach (var shelf in Definitions(DateTime.UtcNow.Year))
        {
            var page = await _client.BrowseAsync(shelf.Filter, ExploreQueries.ShelfPerPage, ct).ConfigureAwait(false);
            shelves.Add(new ExploreShelfDto
            {
                Key = shelf.Key,
                Title = shelf.Title,
                Subtitle = shelf.Subtitle,
                BrowseQuery = shelf.BrowseQuery,
                Items = ExploreLibraryEnricher.Prepare(page.Items, shelf.Filter, state)
            });
        }

        return shelves;
    }

    private static ShelfDefinition[] Definitions(int year) =>
    [
        new("trending", "Trending now", "What readers are into this week", "sort=trending",
            new ExploreFilter { Sort = "trending" }),
        new("top-finished", "Best completed series", "Finished, highest rated", "status=FINISHED&sort=score",
            new ExploreFilter { Status = ["FINISHED"], Sort = "score" }),
        new("recently-finished", "Recently completed", "Just wrapped up", "status=FINISHED&sort=recentlyFinished",
            new ExploreFilter { Status = ["FINISHED"], Sort = "recentlyFinished" }),
        new("hidden-gems", "Hidden gems", "Highly rated, not yet widely read",
            "minScore=78&maxPopularity=15000&sort=score",
            new ExploreFilter { MinScore = 78, MaxPopularity = 15000, Sort = "score" }),
        new("popular-manhwa", "Popular manhwa", "Korean webtoons everyone reads", "type=manhwa&sort=popularity",
            new ExploreFilter { Type = "manhwa", Sort = "popularity" }),
        new("new-this-year", "Started this year", "New series from " + year, $"yearFrom={year}&sort=popularity",
            new ExploreFilter { YearFrom = year, Sort = "popularity" })
    ];
}
