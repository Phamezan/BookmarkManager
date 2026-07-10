using System.Text.Json;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class AniListLibraryProviderTests
{
    [Fact]
    public void ParseSearchResults_MapsCoreFields()
    {
        const string json = """
        {
          "data": {
            "Page": {
              "media": [
                {
                  "id": 101922,
                  "type": "MANGA",
                  "format": "MANGA",
                  "countryOfOrigin": "JP",
                  "title": { "romaji": "Sousou no Frieren", "english": "Frieren: Beyond Journey's End", "native": "葬送のフリーレン" },
                  "coverImage": { "large": "https://example.com/frieren.jpg" },
                  "description": "An elf mage<br>outlives her party.",
                  "genres": ["Adventure", "Drama", "Fantasy"],
                  "averageScore": 90,
                  "status": "RELEASING",
                  "chapters": null,
                  "volumes": null,
                  "episodes": null,
                  "siteUrl": "https://anilist.co/manga/101922",
                  "updatedAt": 1700000000,
                  "staff": {
                    "edges": [
                      { "role": "Story & Art", "node": { "name": { "full": "Kanehito Yamada" } } }
                    ]
                  }
                }
              ]
            }
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var results = AniListLibraryProvider.ParseSearchResults(doc.RootElement, "AniList");

        var entry = Assert.Single(results);
        Assert.Equal("101922", entry.ProviderId);
        Assert.Equal("Sousou no Frieren", entry.Title);
        Assert.Contains("Frieren: Beyond Journey's End", entry.AlternateTitles);
        Assert.Equal(LibraryMediaType.Manga, entry.MediaType);
        Assert.Equal("An elf mage\noutlives her party.", entry.Synopsis?.Trim());
        Assert.Equal(9.0, entry.Rating);
        Assert.Contains("Kanehito Yamada", entry.Authors);
        Assert.Equal("https://anilist.co/manga/101922", entry.SourceUrl);
    }

    [Fact]
    public void ParseSearchResults_ReturnsEmptyWhenStructureUnexpected()
    {
        using var doc = JsonDocument.Parse("""{ "data": {} }""");
        var results = AniListLibraryProvider.ParseSearchResults(doc.RootElement, "AniList");
        Assert.Empty(results);
    }

    [Theory]
    [InlineData("ANIME", null, null, LibraryMediaType.Anime)]
    [InlineData("MANGA", "MANGA", "KR", LibraryMediaType.Manhwa)]
    [InlineData("MANGA", "MANGA", "JP", LibraryMediaType.Manga)]
    [InlineData("MANGA", "NOVEL", "JP", LibraryMediaType.LightNovel)]
    [InlineData("MANGA", "NOVEL", "KR", LibraryMediaType.Webnovel)]
    public void ResolveMediaType_MapsAniListTypeFormatAndCountry(string type, string? format, string? country, LibraryMediaType expected)
    {
        Assert.Equal(expected, AniListLibraryProvider.ResolveMediaType(type, format, country));
    }
}
