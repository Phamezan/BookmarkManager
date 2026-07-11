using System.Text.Json;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class MangaDexLibraryProviderTests
{
    [Fact]
    public void MapManga_ExtractsTitleCoverAuthorAndGenres()
    {
        const string json = """
        {
          "id": "a1c7c817-4e59-43b7-9365-09675a149a6f",
          "type": "manga",
          "attributes": {
            "title": { "en": "Solo Leveling" },
            "altTitles": [ { "ko": "나 혼자만 레벨업" } ],
            "description": { "en": "The weakest hunter levels up." },
            "status": "completed",
            "lastChapter": "200",
            "lastVolume": "14",
            "originalLanguage": "ko",
            "tags": [
              { "attributes": { "name": { "en": "Action" }, "group": "genre" } },
              { "attributes": { "name": { "en": "Long Strip" }, "group": "format" } }
            ]
          },
          "relationships": [
            { "id": "cover-1", "type": "cover_art", "attributes": { "fileName": "cover.jpg" } },
            { "id": "author-1", "type": "author", "attributes": { "name": "Chugong" } }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var entry = MangaDexLibraryProvider.MapManga(doc.RootElement, "MangaDex");

        Assert.NotNull(entry);
        Assert.Equal("Solo Leveling", entry!.Title);
        Assert.Equal(LibraryMediaType.Manhwa, entry.MediaType);
        Assert.Equal("200", entry.LatestChapter);
        Assert.Equal("14", entry.LatestVolume);
        Assert.Equal("completed", entry.Status);
        Assert.Contains("Action", entry.Genres);
        Assert.DoesNotContain("Long Strip", entry.Genres);
        Assert.Equal("Chugong", Assert.Single(entry.Authors));
        Assert.Equal("https://uploads.mangadex.org/covers/a1c7c817-4e59-43b7-9365-09675a149a6f/cover.jpg.256.jpg", entry.CoverImageUrl);
        Assert.Equal("https://mangadex.org/title/a1c7c817-4e59-43b7-9365-09675a149a6f", entry.SourceUrl);
    }

    [Fact]
    public void MapManga_PrefersEnglishFromAltTitlesWhenPrimaryIsOriginalLanguage()
    {
        const string json = """
        {
          "id": "a1c7c817-4e59-43b7-9365-09675a149a6f",
          "type": "manga",
          "attributes": {
            "title": { "ja": "俺だけレベルアップな件" },
            "altTitles": [ { "en": "Solo Leveling" }, { "ko": "나 혼자만 레벨업" } ],
            "description": { "en": "The weakest hunter levels up." },
            "status": "completed",
            "originalLanguage": "ko",
            "tags": []
          },
          "relationships": []
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var entry = MangaDexLibraryProvider.MapManga(doc.RootElement, "MangaDex");

        Assert.NotNull(entry);
        Assert.Equal("Solo Leveling", entry!.Title);
        Assert.Contains("俺だけレベルアップな件", entry.AlternateTitles);
    }

    [Fact]
    public void MapManga_ReturnsNullWhenTitleMissing()
    {
        using var doc = JsonDocument.Parse("""{ "id": "x", "attributes": {} }""");
        Assert.Null(MangaDexLibraryProvider.MapManga(doc.RootElement, "MangaDex"));
    }

    [Fact]
    public void ParseLatestRelease_ReadsFirstFeedChapter()
    {
        const string json = """
        {
          "data": [
            {
              "attributes": {
                "chapter": "201",
                "volume": "15",
                "publishAt": "2024-06-01T12:00:00Z"
              }
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var release = MangaDexLibraryProvider.ParseLatestRelease(doc.RootElement, "manga-id");

        Assert.NotNull(release);
        Assert.Equal("201", release!.LatestChapter);
        Assert.Equal("15", release.LatestVolume);
        Assert.Equal(new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero), release.LastReleaseAt);
    }

    [Fact]
    public void ParseLatestRelease_ReturnsNullWhenFeedEmpty()
    {
        using var doc = JsonDocument.Parse("""{ "data": [] }""");
        Assert.Null(MangaDexLibraryProvider.ParseLatestRelease(doc.RootElement, "manga-id"));
    }
}
