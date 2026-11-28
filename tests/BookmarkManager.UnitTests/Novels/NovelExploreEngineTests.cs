using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Novels;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.UnitTests.Novels;

public class NovelExploreEngineTests
{
    [Fact]
    public void BuildRows_NormalizesPopularityAcrossProvidersAndInterleaves()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new List<LibraryCatalogEntry>
        {
            MakeEntry("RanobeDB", "r1", "Ranobe #1", 1, now),
            MakeEntry("RanobeDB", "r2", "Ranobe #2", 2, now),
            MakeEntry("Novelfire", "n1", "Novelfire #1", 10, now),
            MakeEntry("Novelfire", "n2", "Novelfire #2", 20, now),
            MakeEntry("Novelfire", "n3", "Novelfire Unranked", null, now)
        };

        var rows = NovelCatalogEngine.BuildRows(entries);
        var byTitle = rows.ToDictionary(r => r.Title);

        Assert.Equal(0.5, byTitle["Ranobe #1"].PopularityScore, 4);
        Assert.Equal(1.0, byTitle["Ranobe #2"].PopularityScore, 4);
        Assert.Equal(0.5, byTitle["Novelfire #1"].PopularityScore, 4);
        Assert.Equal(1.0, byTitle["Novelfire #2"].PopularityScore, 4);
        Assert.Equal(1.0, byTitle["Novelfire Unranked"].PopularityScore, 4);

        var ordered = NovelCatalogEngine.Sort(rows, NovelCatalogFilter.SortPopular)
            .Select(r => r.Title)
            .ToArray();

        // Both providers' #1 (score 0.5) come before either provider's #2 (score 1.0); unranked sorts last.
        Assert.Equal(
            ["Novelfire #1", "Ranobe #1", "Novelfire #2", "Ranobe #2", "Novelfire Unranked"],
            ordered);
    }

    [Theory]
    [InlineData("270", 270)]
    [InlineData("Ch 270", 270)]
    [InlineData("Ch. 42.5", 42)]
    [InlineData("Chapter 3090 Born Into an Endless War", 3090)]
    [InlineData("1200 Chapters", 1200)]
    [InlineData("85 ch", 85)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Prologue", null)]
    [InlineData("Vol. 1 Ch. 5", null)]
    public void ParseChapters_HandlesValidAndUnparsableValues(string? input, int? expected)
    {
        Assert.Equal(expected, NovelCatalogEngine.ParseChapters(input));
    }

    [Theory]
    [InlineData("Novelfire", "https://novelfire.net/book/shadow-slave", true)]
    [InlineData("novelfire", "https://novelfire.net/book/lord-of-mysteries", true)]
    [InlineData("Novelfire", "https://novelfire.net/book/", false)]
    [InlineData("Novelfire", "https://novelfire.net/genre/action", false)]
    [InlineData("RanobeDB", "https://novelfire.net/book/shadow-slave", false)]
    [InlineData("Novelfire", "https://example.com/book/shadow-slave", false)]
    public void IsNovelfireBookUrl_MatchesOnlyValidNovelfireBookPages(string provider, string url, bool expected)
    {
        Assert.Equal(expected, NovelCatalogEngine.IsNovelfireBookUrl(provider, url));
    }

    [Fact]
    public void ScoreByGenreProfile_WeightsNormalizesTieBreaksAndSetsGenreReasons()
    {
        var seeds = new List<NovelCatalogRow>
        {
            MakeRow("Seed A", ["Action", "Fantasy"], 0.1),
            MakeRow("Seed B", ["Action", "LitRPG"], 0.2)
        };
        // Weights: Action=2, Fantasy=1, LitRPG=1

        var focused = MakeRow("Focused Match", ["Action", "Fantasy"], 0.5);       // (2+1)/2 = 1.5
        var diluted = MakeRow("Diluted Match", ["Action", "Fantasy", "Comedy", "Drama"], 0.1); // (2+1)/4 = 0.75
        var tiePopular = MakeRow("Tie Popular", ["Action", "LitRPG"], 0.2);       // (2+1)/2 = 1.5, pop 0.2 < 0.5
        var unrelated = MakeRow("Unrelated", ["Romance", "Slice of Life"], 0.05); // 0 -> excluded
        var emptyGenres = MakeRow("No Genres", [], 0.01);                         // 0 -> excluded

        var ranked = NovelForYouScorer.ScoreByGenreProfile(
            seeds,
            [focused, diluted, tiePopular, unrelated, emptyGenres]);

        Assert.Equal(["Tie Popular", "Focused Match", "Diluted Match"], ranked.Select(r => r.Row.Title).ToArray());
        Assert.Equal(["Genre: Action", "Genre: LitRPG"], ranked[0].BecauseOf);
        Assert.Equal(["Genre: Action", "Genre: Fantasy"], ranked[1].BecauseOf);
    }

    [Fact]
    public void ScoreByGenreProfile_EmptySeedGenresReturnsEmpty()
    {
        var seeds = new List<NovelCatalogRow> { MakeRow("Seed", [], 0.1) };
        var candidates = new List<NovelCatalogRow> { MakeRow("Candidate", ["Action"], 0.1) };

        Assert.Empty(NovelForYouScorer.ScoreByGenreProfile(seeds, candidates));
    }

    private static LibraryCatalogEntry MakeEntry(
        string provider,
        string providerId,
        string title,
        int? rank,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Provider = provider,
        ProviderId = providerId,
        Title = title,
        MediaType = LibraryMediaType.Webnovel,
        PopularityRank = rank,
        SourceUrl = $"https://example.com/{providerId}",
        FirstImportedAt = now,
        LastRefreshedAt = now
    };

    private static NovelCatalogRow MakeRow(string title, string[] genres, double popularityScore) => new()
    {
        Id = Guid.NewGuid(),
        Provider = "Novelfire",
        ProviderId = title.ToLowerInvariant().Replace(' ', '-'),
        Title = title,
        Genres = genres,
        GenreSet = new HashSet<string>(genres, StringComparer.OrdinalIgnoreCase),
        PopularityScore = popularityScore,
        IsNovelfireBookUrl = true
    };
}
