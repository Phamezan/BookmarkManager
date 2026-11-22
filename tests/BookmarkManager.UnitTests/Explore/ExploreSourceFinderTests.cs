using BookmarkManager.Api.Services.Explore;
using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Contracts;
using static BookmarkManager.UnitTests.Explore.ExploreTestSupport;

namespace BookmarkManager.UnitTests.Explore;

public sealed class ExploreSourceFinderTests
{
    private static readonly ExploreItemDto Series = new()
    {
        AnilistId = 1,
        Title = "Solo Leveling",
        RomajiTitle = "Na Honjaman Lebel Eop",
        NativeTitle = "native",
        Synonyms = ["Only I Level Up", "Arise", "Third Synonym"]
    };

    private static (ExploreSourceFinder Finder, FakeSuwayomiClient Client) Build()
    {
        var client = new FakeSuwayomiClient();
        client.Sources.Add(new SuwayomiSource("1", "SrcA", "en"));
        client.Sources.Add(new SuwayomiSource("2", "SrcB", "en"));
        var finder = new ExploreSourceFinder(AniList(new FakeAniListHandler()), Searcher(client, "SrcA", "SrcB"));
        return (finder, client);
    }

    [Fact]
    public void QueriesAreEnglishRomajiThenTwoSynonyms()
        => Assert.Equal(
            ["Solo Leveling", "Na Honjaman Lebel Eop", "Only I Level Up", "Arise"],
            ExploreSourceFinder.BuildQueries(Series));

    [Fact]
    public void DuplicateAndBlankQueriesAreSkipped()
    {
        var series = new ExploreItemDto { Title = "Same", RomajiTitle = "same!", Synonyms = ["", "Other"] };

        Assert.Equal(["Same", "Other"], ExploreSourceFinder.BuildQueries(series));
    }

    [Fact]
    public async Task StopsAtTheFirstQueryThatYieldsHits()
    {
        var (finder, client) = Build();
        client.HitsByQuery["Na Honjaman Lebel Eop"] = [new SuwayomiManga(10, "Other Name", false, null, null)];

        var hits = await finder.FindAsync(Series, CancellationToken.None);

        Assert.Equal(["Solo Leveling", "Solo Leveling", "Na Honjaman Lebel Eop", "Na Honjaman Lebel Eop"], client.Searched);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task NoHitsAnywhereTriesEveryQueryAndReturnsEmpty()
    {
        var (finder, client) = Build();

        var hits = await finder.FindAsync(Series, CancellationToken.None);

        Assert.Empty(hits);
        Assert.Equal(8, client.Searched.Count);
    }

    [Fact]
    public async Task ExactMatchesComeFirstThenSourceOrder()
    {
        var (finder, client) = Build();
        client.HitsByQuery["Solo Leveling"] =
        [
            new SuwayomiManga(1, "Solo Leveling: Ragnarok", false, null, null),
            new SuwayomiManga(2, "solo leveling", true, null, null)
        ];

        var hits = await finder.FindAsync(Series, CancellationToken.None);

        // SrcA then SrcB each return the same two hits; exact ones first, source order inside each group.
        Assert.Equal([2, 2, 1, 1], hits.Select(h => h.MangaId));
        Assert.Equal(["SrcA", "SrcB", "SrcA", "SrcB"], hits.Select(h => h.SourceName));
        Assert.Equal([true, true, false, false], hits.Select(h => h.IsExactMatch));
        Assert.True(hits[0].InLibrary);
    }

    [Fact]
    public async Task SynonymTitleCountsAsExact()
    {
        var (finder, client) = Build();
        client.HitsByQuery["Solo Leveling"] = [new SuwayomiManga(3, "Only I Level Up", false, null, null)];

        var hits = await finder.FindAsync(Series, CancellationToken.None);

        Assert.All(hits, h => Assert.True(h.IsExactMatch));
    }
}
