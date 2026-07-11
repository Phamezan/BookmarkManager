using System.Text.Json;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class KitsuLibraryProviderTests
{
    [Fact]
    public void MapResource_MangaSubtypeNovelMapsToLightNovel()
    {
        const string json = """
        {
          "id": "12345",
          "attributes": {
            "canonicalTitle": "God of Fishing",
            "titles": { "en": "God of Fishing", "en_jp": "God of Fishing" },
            "synopsis": "A retired pro angler gets a second chance.",
            "posterImage": { "large": "https://example.com/cover.jpg" },
            "averageRating": "82.5",
            "status": "current",
            "subtype": "novel",
            "chapterCount": 120,
            "volumeCount": null,
            "slug": "god-of-fishing"
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var entry = KitsuLibraryProvider.MapResource(doc.RootElement, "manga", "Kitsu");

        Assert.NotNull(entry);
        Assert.Equal("manga:12345", entry!.ProviderId);
        Assert.Equal("God of Fishing", entry.Title);
        Assert.Equal(LibraryMediaType.LightNovel, entry.MediaType);
        Assert.Equal(8.2, entry.Rating);
        Assert.Equal("120", entry.LatestChapter);
        Assert.Equal("https://kitsu.io/manga/god-of-fishing", entry.SourceUrl);
    }

    [Fact]
    public void MapResource_AnimeResourceUsesEpisodeCountAsLatestChapter()
    {
        const string json = """
        {
          "id": "999",
          "attributes": {
            "canonicalTitle": "Naruto Shippuden",
            "titles": {},
            "subtype": "TV",
            "episodeCount": 500,
            "slug": "naruto-shippuden"
          }
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var entry = KitsuLibraryProvider.MapResource(doc.RootElement, "anime", "Kitsu");

        Assert.NotNull(entry);
        Assert.Equal(LibraryMediaType.Anime, entry!.MediaType);
        Assert.Equal("500", entry.LatestChapter);
        Assert.Equal("anime:999", entry.ProviderId);
    }

    [Fact]
    public void MapResource_ReturnsNullWhenTitleMissing()
    {
        using var doc = JsonDocument.Parse("""{ "id": "1", "attributes": {} }""");
        Assert.Null(KitsuLibraryProvider.MapResource(doc.RootElement, "manga", "Kitsu"));
    }

    [Theory]
    [InlineData("manga:12345", true, "manga", "12345")]
    [InlineData("anime:999", true, "anime", "999")]
    [InlineData("novel:1", false, "manga", "")]
    [InlineData("notvalid", false, "manga", "")]
    public void TryParseProviderId_ParsesResourceTypeAndId(string providerId, bool expectedSuccess, string expectedResourceType, string expectedId)
    {
        var success = KitsuLibraryProvider.TryParseProviderId(providerId, out var resourceType, out var id);

        Assert.Equal(expectedSuccess, success);
        if (expectedSuccess)
        {
            Assert.Equal(expectedResourceType, resourceType);
            Assert.Equal(expectedId, id);
        }
    }
}
