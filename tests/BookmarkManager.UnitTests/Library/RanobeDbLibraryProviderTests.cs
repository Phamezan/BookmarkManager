using System.Text.Json;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class RanobeDbLibraryProviderTests
{
    [Fact]
    public void MapSeriesSummary_ExtractsTitleCoverAndVolumeCount()
    {
        const string json = """
        {
          "book": { "id": 7378, "image": { "id": 7378, "filename": "otFlxUvL0iahjePs.jpg" } },
          "volumes": { "count": 20 },
          "id": 1844,
          "romaji_orig": "Yahari Ore no Seishun LoveCome wa Machigatte Iru.",
          "title": "My Youth Romantic Comedy Is Wrong, As I Expected",
          "title_orig": "\u3084\u306f\u308a\u4fd8\u304c\u306e\u9752\u6625\u30e9\u30d6\u30b3\u30d0\u306f\u307e\u3061\u304c\u3063\u3066\u3044\u308b\u3002",
          "c_num_books": 20,
          "c_start_date": 20110399,
          "c_end_date": 99999999
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var entry = RanobeDbLibraryProvider.MapSeriesSummary(doc.RootElement, "RanobeDB");

        Assert.NotNull(entry);
        Assert.Equal("My Youth Romantic Comedy Is Wrong, As I Expected", entry!.Title);
        Assert.Equal(LibraryMediaType.LightNovel, entry.MediaType);
        Assert.Equal("20", entry.LatestVolume);
        Assert.Contains("Yahari Ore no Seishun LoveCome wa Machigatte Iru.", entry.AlternateTitles);
        Assert.Equal("https://images.ranobedb.org/otFlxUvL0iahjePs.jpg", entry.CoverImageUrl);
        Assert.Equal("https://ranobedb.org/series/1844", entry.SourceUrl);
        // c_end_date of 99999999 means "no end date" - falls back to c_start_date (unknown day -> 1st).
        Assert.Equal(new DateTimeOffset(2011, 3, 1, 0, 0, 0, TimeSpan.Zero), entry.LastReleaseAt);
    }

    [Fact]
    public void MapSeriesSummary_ReturnsNullWhenTitleMissing()
    {
        using var doc = JsonDocument.Parse("""{ "id": 1 }""");
        Assert.Null(RanobeDbLibraryProvider.MapSeriesSummary(doc.RootElement, "RanobeDB"));
    }

    [Fact]
    public void MapSeriesDetails_ExtractsGenresAuthorsStatusAndLatestVolume()
    {
        const string json = """
        {
          "id": 1844,
          "title": "My Youth Romantic Comedy Is Wrong, As I Expected",
          "publication_status": "ongoing",
          "description": "",
          "book_description": { "description": "Hachiman Hikigaya is a cynic." },
          "titles": [
            { "title": "Oregairu", "lang": "en" }
          ],
          "books": [
            { "sort_order": 1, "c_release_date": 20110399, "image": { "filename": "cover1.jpg" } },
            { "sort_order": 2, "c_release_date": 20170523, "image": { "filename": "cover2.jpg" } }
          ],
          "staff": [
            { "role_type": "author", "romaji": "Watari Wataru", "name": "\u6e21 \u822a\u3080" },
            { "role_type": "artist", "romaji": "Ponkan 8", "name": "\u307c\u3093\u304b\u30938" },
            { "role_type": "translator", "romaji": "Jennifer Ward", "name": "Jennifer Ward" }
          ],
          "tags": [
            { "name": "comedy", "ttype": "genre" },
            { "name": "romance", "ttype": "genre" },
            { "name": "male protagonist", "ttype": "tag" }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var entry = RanobeDbLibraryProvider.MapSeriesDetails(doc.RootElement, "RanobeDB");

        Assert.NotNull(entry);
        Assert.Equal("ongoing", entry!.Status);
        Assert.Equal("Hachiman Hikigaya is a cynic.", entry.Synopsis);
        Assert.Contains("comedy", entry.Genres);
        Assert.Contains("romance", entry.Genres);
        Assert.DoesNotContain("male protagonist", entry.Genres);
        Assert.Contains("Watari Wataru", entry.Authors);
        Assert.Contains("Ponkan 8", entry.Authors);
        Assert.DoesNotContain("Jennifer Ward", entry.Authors);
        // Cover always comes from volume 1 (sort_order 1) regardless of which volume released most
        // recently; LatestVolume reflects the most recently released book's sort order instead.
        Assert.Equal("https://images.ranobedb.org/cover1.jpg", entry.CoverImageUrl);
        Assert.Equal("2", entry.LatestVolume);
        Assert.Equal(new DateTimeOffset(2017, 5, 23, 0, 0, 0, TimeSpan.Zero), entry.LastReleaseAt);
    }

    [Fact]
    public void MapSeriesDetails_PrefersEnglishFromTitlesArrayWhenPrimaryDiffers()
    {
        const string json = """
        {
          "id": 1234,
          "title": "\u30bd\u30fc\u30c9\u30fb\u30a2\u30fc\u30c8\u30fb\u30aa\u30f3\u30e9\u30a4\u30f3",
          "publication_status": "ongoing",
          "titles": [
            { "title": "\u30bd\u30fc\u30c9\u30fb\u30a2\u30fc\u30c8\u30fb\u30aa\u30f3\u30e9\u30a4\u30f3", "lang": "ja", "official": true },
            { "title": "Sword Art Online", "lang": "en", "official": true }
          ],
          "books": [],
          "staff": [],
          "tags": []
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var entry = RanobeDbLibraryProvider.MapSeriesDetails(doc.RootElement, "RanobeDB");

        Assert.NotNull(entry);
        Assert.Equal("Sword Art Online", entry!.Title);
        Assert.Contains("\u30bd\u30fc\u30c9\u30fb\u30a2\u30fc\u30c8\u30fb\u30aa\u30f3\u30e9\u30a4\u30f3", entry.AlternateTitles);
    }

    [Fact]
    public void MapSeriesDetails_ReturnsNullWhenTitleMissing()
    {
        using var doc = JsonDocument.Parse("""{ "id": 1 }""");
        Assert.Null(RanobeDbLibraryProvider.MapSeriesDetails(doc.RootElement, "RanobeDB"));
    }

    [Theory]
    [InlineData(20110399, 2011, 3, 1)]
    [InlineData(20170523, 2017, 5, 23)]
    [InlineData(99999999, null, null, null)]
    [InlineData(0, null, null, null)]
    public void ParseCompactDate_HandlesUnknownDayAndSentinelValues(int value, int? year, int? month, int? day)
    {
        var result = RanobeDbLibraryProvider.ParseCompactDate(value);

        if (year is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Equal(new DateTimeOffset(year.Value, month!.Value, day!.Value, 0, 0, 0, TimeSpan.Zero), result);
        }
    }

    [Fact]
    public void ParseSeriesArray_SkipsEntriesMissingTitleAndPreservesOrder()
    {
        const string json = """
        {
          "series": [
            { "id": 1, "title": "First" },
            { "id": 2 },
            { "id": 3, "title": "Third" }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var entries = RanobeDbLibraryProvider.ParseSeriesArray(doc, "RanobeDB");

        Assert.Equal(2, entries.Count);
        Assert.Equal("First", entries[0].Title);
        Assert.Equal("Third", entries[1].Title);
    }
}
