using System.Text.Json;
using BookmarkManager.Api.Services.Explore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace BookmarkManager.UnitTests.Explore;

public sealed class ExploreFilterTests
{
    private static Dictionary<string, object?> Vars(params (string Key, string Value)[] query)
    {
        var collection = new QueryCollection(query.ToDictionary(q => q.Key, q => new StringValues(q.Value)));
        return ExploreQueries.BuildBrowseVariables(ExploreFilter.FromQuery(collection), ExploreQueries.BrowsePerPage);
    }

    [Theory]
    [InlineData("manhwa", "KR")]
    [InlineData("manhua", "CN")]
    [InlineData("manga", "JP")]
    public void TypeMapsToCountryOfOrigin(string type, string country)
        => Assert.Equal(country, Vars(("type", type))["countryOfOrigin"]);

    [Fact]
    public void AllOrUnknownTypeSetsNoCountry()
    {
        Assert.DoesNotContain("countryOfOrigin", Vars(("type", "all")).Keys);
        Assert.DoesNotContain("countryOfOrigin", Vars(("type", "bogus")).Keys);
    }

    [Fact]
    public void DefaultsExcludeNovelsAndAdultAndSortByPopularity()
    {
        var vars = Vars();

        Assert.Equal(new[] { "NOVEL" }, (string[])vars["formatNotIn"]!);
        Assert.Equal(false, vars["isAdult"]);
        Assert.Equal(new[] { "POPULARITY_DESC" }, (string[])vars["sort"]!);
        Assert.Equal(1, vars["page"]);
        Assert.Equal(30, vars["perPage"]);
    }

    [Fact]
    public void AdultTrueDropsTheAdultFilterButKeepsNovelExclusion()
    {
        var vars = Vars(("adult", "true"));

        Assert.DoesNotContain("isAdult", vars.Keys);
        Assert.Contains("formatNotIn", vars.Keys);
    }

    [Fact]
    public void StatusListIsSplitUppercasedAndValidated()
    {
        var vars = Vars(("status", "finished, RELEASING,bogus"));

        Assert.Equal(new[] { "FINISHED", "RELEASING" }, (string[])vars["statusIn"]!);
    }

    [Fact]
    public void TriStateGenresAndTagsMapToIncludeAndExcludeLists()
    {
        var vars = Vars(
            ("genres", "Action,Fantasy"), ("excludeGenres", "Horror"),
            ("tags", "Isekai"), ("excludeTags", "Gore,Harem"));

        Assert.Equal(new[] { "Action", "Fantasy" }, (string[])vars["genreIn"]!);
        Assert.Equal(new[] { "Horror" }, (string[])vars["genreNotIn"]!);
        Assert.Equal(new[] { "Isekai" }, (string[])vars["tagIn"]!);
        Assert.Equal(new[] { "Gore", "Harem" }, (string[])vars["tagNotIn"]!);
    }

    [Fact]
    public void NumericBoundsBecomeInclusiveAniListBounds()
    {
        var vars = Vars(
            ("minScore", "75"), ("minChapters", "50"), ("maxChapters", "150"),
            ("yearFrom", "2018"), ("yearTo", "2020"), ("maxPopularity", "15000"));

        Assert.Equal(74, vars["averageScoreGreater"]);
        Assert.Equal(49, vars["chaptersGreater"]);
        Assert.Equal(151, vars["chaptersLesser"]);
        Assert.Equal(20179999, vars["startDateGreater"]);
        Assert.Equal(20210000, vars["startDateLesser"]);
        Assert.Equal(15000, vars["popularityLesser"]);
    }

    [Theory]
    [InlineData("popularity", "POPULARITY_DESC")]
    [InlineData("score", "SCORE_DESC")]
    [InlineData("trending", "TRENDING_DESC")]
    [InlineData("newest", "START_DATE_DESC")]
    [InlineData("updated", "UPDATED_AT_DESC")]
    [InlineData("title", "TITLE_ENGLISH")]
    [InlineData("chapters", "CHAPTERS_DESC")]
    [InlineData("favourites", "FAVOURITES_DESC")]
    [InlineData("recentlyFinished", "END_DATE_DESC")]
    public void SortKeysMapToAniListSort(string key, string expected)
        => Assert.Equal(new[] { expected }, (string[])Vars(("sort", key))["sort"]!);

    [Fact]
    public void SearchWithoutSortUsesSearchMatch()
    {
        var vars = Vars(("q", "solo"));

        Assert.Equal("solo", vars["search"]);
        Assert.Equal(new[] { "SEARCH_MATCH" }, (string[])vars["sort"]!);
    }

    [Fact]
    public void SearchWithExplicitSortKeepsTheSort()
        => Assert.Equal(new[] { "SCORE_DESC" }, (string[])Vars(("q", "solo"), ("sort", "score"))["sort"]!);

    [Fact]
    public void InvalidValuesAreIgnored()
    {
        var vars = Vars(
            ("minScore", "abc"), ("minChapters", "-5"), ("yearFrom", "20"), ("page", "0"),
            ("sort", "nonsense"), ("type", "novel"), ("status", "nope"), ("adult", "maybe"));

        Assert.Equal(1, vars["page"]);
        Assert.Equal(new[] { "POPULARITY_DESC" }, (string[])vars["sort"]!);
        Assert.Equal(false, vars["isAdult"]);
        foreach (var key in new[] { "averageScoreGreater", "chaptersGreater", "startDateGreater", "statusIn", "countryOfOrigin" })
        {
            Assert.DoesNotContain(key, vars.Keys);
        }
    }

    [Fact]
    public void HideDismissedDefaultsToTrueAndHideLibraryToFalse()
    {
        var filter = ExploreFilter.FromQuery(new QueryCollection());

        Assert.True(filter.HideDismissed);
        Assert.False(filter.HideLibrary);
        Assert.False(ExploreFilter.FromQuery(new QueryCollection(
            new Dictionary<string, StringValues> { ["hideDismissed"] = "false" })).HideDismissed);
    }

    [Fact]
    public void VariablesSerializeToJsonWithoutNullEntries()
    {
        var json = JsonSerializer.Serialize(Vars(("q", "x\"; DROP")));

        Assert.DoesNotContain("null", json);
        Assert.Contains("x\\u0022; DROP", json);
    }

    [Theory]
    [InlineData("KR", "Manhwa")]
    [InlineData("CN", "Manhua")]
    [InlineData("TW", "Manhua")]
    [InlineData("JP", "Manga")]
    [InlineData("FR", "Unknown")]
    [InlineData(null, "Unknown")]
    public void CountryMapsToType(string? country, string expected)
        => Assert.Equal(expected, ExploreTypes.FromCountry(country));
}
