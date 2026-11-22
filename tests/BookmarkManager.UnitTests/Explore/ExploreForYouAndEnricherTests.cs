using BookmarkManager.Api.Services.Explore;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Contracts;
using static BookmarkManager.UnitTests.Explore.ExploreTestSupport;

namespace BookmarkManager.UnitTests.Explore;

public sealed class ExploreForYouAndEnricherTests
{
    private static ForYouRecommendation Rec(int rating, int id, string title, bool adult = false)
        => new(rating, ParseElement(Media(id, title, adult: adult)));

    [Fact]
    public void ScoreIsTheSumOfRatingsAcrossLibrarySourcesBestFirst()
    {
        var sources = new[]
        {
            new ForYouSource("Lib A", [Rec(10, 1, "Alpha"), Rec(50, 2, "Beta")]),
            new ForYouSource("Lib B", [Rec(45, 1, "Alpha")])
        };

        var ranked = ExploreForYouRecommender.Rank(sources, new HashSet<int>());

        Assert.Equal([1, 2], ranked.Select(r => r.AnilistId));
    }

    [Fact]
    public void NonPositiveRatingsCountAsOne()
    {
        var sources = new[]
        {
            new ForYouSource("Lib A", [Rec(0, 1, "Alpha"), Rec(-3, 2, "Beta"), Rec(0, 2, "Beta")])
        };

        var ranked = ExploreForYouRecommender.Rank(sources, new HashSet<int>());

        Assert.Equal(2, ranked[0].AnilistId);
    }

    [Fact]
    public void LibraryDismissedAndAdultAreExcluded()
    {
        var sources = new[]
        {
            new ForYouSource("Lib A",
            [
                Rec(90, 1, "InLibrary"), Rec(80, 2, "Dismissed"), Rec(70, 3, "Adult", adult: true), Rec(5, 4, "Keeper")
            ])
        };

        var ranked = ExploreForYouRecommender.Rank(sources, new HashSet<int> { 1, 2 });

        Assert.Equal([4], ranked.Select(r => r.AnilistId));
    }

    [Fact]
    public void BecauseOfListsUpToThreeLibraryTitlesHighestRatingFirst()
    {
        var sources = new[]
        {
            new ForYouSource("Low", [Rec(1, 9, "Target")]),
            new ForYouSource("High", [Rec(90, 9, "Target")]),
            new ForYouSource("Mid", [Rec(40, 9, "Target")]),
            new ForYouSource("Lower", [Rec(5, 9, "Target")])
        };

        var ranked = ExploreForYouRecommender.Rank(sources, new HashSet<int>());

        Assert.Equal(["High", "Mid", "Lower"], ranked.Single().BecauseOf);
    }

    [Fact]
    public async Task LoadSourcesBatchesAtFiftyAndCachesTheResponses()
    {
        var handler = new FakeAniListHandler
        {
            Respond = _ => (System.Net.HttpStatusCode.OK, "{\"data\":{\"Page\":{\"media\":[{\"id\":1,\"title\":{\"english\":\"Lib\"},\"recommendations\":{\"nodes\":[{\"rating\":3,\"mediaRecommendation\":"
                + Media(7, "Rec") + "}]}}]}}}")
        };
        var recommender = new ExploreForYouRecommender(AniList(handler));
        var ids = Enumerable.Range(1, 120).ToList();

        var first = await recommender.LoadSourcesAsync(ids, CancellationToken.None);
        await recommender.LoadSourcesAsync(ids, CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(50, handler.Variables(0).GetProperty("ids").GetArrayLength());
        Assert.Equal(20, handler.Variables(2).GetProperty("ids").GetArrayLength());
        Assert.Equal(3, first.Count);
    }

    private static ExploreUserState State(
        Dictionary<int, DiscoverProgressValue>? byId = null,
        Dictionary<string, DiscoverProgressValue>? byTitle = null,
        Dictionary<int, string>? list = null)
        => new(byId ?? [], byTitle ?? [], list ?? []);

    [Fact]
    public void EnrichesByAnilistIdAndListState()
    {
        var item = new ExploreItemDto { AnilistId = 5, Title = "Nope" };
        var state = State(
            byId: new() { [5] = new DiscoverProgressValue(12, 40, 77, "Lib", "MangaDex") },
            list: new() { [5] = "saved" });

        ExploreLibraryEnricher.Apply([item], state);

        Assert.Equal(77, item.Library!.MangaId);
        Assert.Equal(12, item.Library.Read);
        Assert.Equal(40, item.Library.Latest);
        Assert.Equal("MangaDex", item.Library.SourceName);
        Assert.Equal("saved", item.ListState);
    }

    [Fact]
    public void FallsBackToNormalizedAlternateTitles()
    {
        var item = new ExploreItemDto
        {
            AnilistId = 6,
            Title = "Unmatched English",
            RomajiTitle = "Unmatched Romaji",
            Synonyms = ["The Divine Demon's Ascension"]
        };
        var key = MediaTitleNormalizer.NormalizeForSearch("the divine demons ascension");
        var state = State(byTitle: new() { [key] = new DiscoverProgressValue(1, 2, 88) });

        ExploreLibraryEnricher.Apply([item], state);

        Assert.Equal(88, item.Library!.MangaId);
        Assert.Null(item.ListState);
    }

    [Fact]
    public void ApplyClearsStaleStateOnReusedItems()
    {
        var item = new ExploreItemDto
        {
            AnilistId = 1,
            Library = new ExploreLibraryDto { MangaId = 3 },
            ListState = "saved"
        };

        ExploreLibraryEnricher.Apply([item], State());

        Assert.Null(item.Library);
        Assert.Null(item.ListState);
    }

    [Fact]
    public void PrepareHonoursHideDismissedAndHideLibrary()
    {
        var items = new[]
        {
            new ExploreItemDto { AnilistId = 1, Title = "Dismissed" },
            new ExploreItemDto { AnilistId = 2, Title = "InLibrary" },
            new ExploreItemDto { AnilistId = 3, Title = "Plain" }
        };
        var state = State(
            byId: new() { [2] = new DiscoverProgressValue(0, 0, 9) },
            list: new() { [1] = "dismissed" });

        var defaults = ExploreLibraryEnricher.Prepare(items, new ExploreFilter(), state);
        var all = ExploreLibraryEnricher.Prepare(
            items, new ExploreFilter { HideDismissed = false, HideLibrary = true }, state);

        Assert.Equal([2, 3], defaults.Select(i => i.AnilistId));
        Assert.Equal([1, 3], all.Select(i => i.AnilistId));
    }
}
