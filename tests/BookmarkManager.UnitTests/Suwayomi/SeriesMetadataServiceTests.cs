using BookmarkManager.Api.Services.Suwayomi;

namespace BookmarkManager.UnitTests.Suwayomi;

public sealed class SeriesMetadataServiceTests
{
    private const string Fixture = """
    [
      {
        "id": 99001,
        "countryOfOrigin": "JP",
        "title": { "romaji": "Tensei Shitara Ken Deshita", "english": null, "native": "転生したら剣でした" },
        "synonyms": ["Reincarnated as a Sword"]
      },
      {
        "id": 99002,
        "countryOfOrigin": "KR",
        "title": { "romaji": "Cheonma Hwangje", "english": "Heavenly Demon Sovereign", "native": null },
        "synonyms": ["The Divine Demon's Grand Ascension", "Sovereign of the Heavenly Demons"]
      }
    ]
    """;

    [Fact]
    public void AcceptsWhenEnglishTitleMatches()
    {
        var result = SeriesMetadataService.SelectAccepted("Heavenly Demon Sovereign", Fixture);

        Assert.True(result.Found);
        Assert.Equal(99002, result.AnilistId);
        Assert.Equal("KR", result.CountryOfOrigin);
    }

    [Fact]
    public void AcceptsWhenSynonymMatches()
    {
        var result = SeriesMetadataService.SelectAccepted("The Divine Demon's Grand Ascension", Fixture);

        Assert.True(result.Found);
        Assert.Equal(99002, result.AnilistId);
    }

    [Fact]
    public void AcceptsWhenSynonymSpellingDiffersByApostropheAndCase()
    {
        var result = SeriesMetadataService.SelectAccepted("the divine demons grand ascension", Fixture);

        Assert.True(result.Found);
        Assert.Equal(99002, result.AnilistId);
    }

    [Fact]
    public void NearMissIsRejected()
    {
        var result = SeriesMetadataService.SelectAccepted("Heavenly Demon", Fixture);

        Assert.False(result.Found);
        Assert.Null(result.AnilistId);
        Assert.Null(result.CountryOfOrigin);
    }

    [Fact]
    public void EmptyResultsAreAMiss()
    {
        var result = SeriesMetadataService.SelectAccepted("Heavenly Demon Sovereign", "[]");

        Assert.False(result.Found);
        Assert.Empty(result.Titles);
    }

    [Fact]
    public void FirstAcceptedCandidateWins()
    {
        const string overlapping = """
        [
          { "id": 1, "countryOfOrigin": "KR", "title": { "romaji": "Alpha", "english": "Alpha", "native": null }, "synonyms": [] },
          { "id": 2, "countryOfOrigin": "JP", "title": { "romaji": "Alpha", "english": "Alpha", "native": null }, "synonyms": [] }
        ]
        """;

        var result = SeriesMetadataService.SelectAccepted("Alpha", overlapping);

        Assert.True(result.Found);
        Assert.Equal(1, result.AnilistId);
        Assert.Equal("KR", result.CountryOfOrigin);
    }

    [Fact]
    public void TitlesRoundTripThroughJson()
    {
        var result = SeriesMetadataService.SelectAccepted("Heavenly Demon Sovereign", Fixture);
        var json = SeriesMetadataService.SerializeTitles(result.Titles);

        var parsed = SeriesMetadataService.ParseTitles(json);

        Assert.Contains("Heavenly Demon Sovereign", parsed);
        Assert.Contains("The Divine Demon's Grand Ascension", parsed);
        Assert.Contains("Cheonma Hwangje", parsed);
    }

    [Fact]
    public void ParseTitlesIsEmptyForGarbage()
    {
        Assert.Empty(SeriesMetadataService.ParseTitles(null));
        Assert.Empty(SeriesMetadataService.ParseTitles("not json"));
        Assert.Empty(SeriesMetadataService.ParseTitles("{\"a\":1}"));
    }
}
