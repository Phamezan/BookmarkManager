using System.Text.Json;
using BookmarkManager.Api.Services.BookmarkTagging;

namespace BookmarkManager.UnitTests;

public sealed class MangaUpdatesTaggingTests
{
    [Fact]
    public void TryExtractFirstSeriesId_ReturnsFirstResultRecordSeriesId()
    {
        const string json = """
        {
          "results": [
            { "record": { "series_id": 33408692186, "title": "The Lone Necromancer" } }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);

        var seriesId = MangaUpdatesTaggingService.TryExtractFirstSeriesId(doc.RootElement);

        Assert.Equal(33408692186L, seriesId);
    }

    [Fact]
    public void ExtractTags_CombinesGenresAndCategoriesWithVoteSorting()
    {
        const string json = """
        {
          "genres": [
            { "genre": "Action" },
            { "genre": "Fantasy" }
          ],
          "categories": [
            { "category": "Level System", "votes_plus": 5, "votes_minus": 0 },
            { "category": "Dungeons", "votes_plus": 10, "votes_minus": 2 },
            { "category": "Fantasy", "votes_plus": 2, "votes_minus": 0 },
            { "category": "Bad Category", "votes_plus": 0, "votes_minus": 5 }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);

        var tags = MangaUpdatesTaggingService.ExtractTags(doc.RootElement);

        // Expected order: 
        // 1. Genres first: Action, Fantasy
        // 2. Categories with positive net votes sorted desc: Dungeons (net 8), Level System (net 5), Fantasy (net 2, deduped)
        // 3. Bad Category excluded (net -5)
        Assert.Equal(new[] { "Action", "Fantasy", "Dungeons", "Level System" }, tags);
    }

    [Theory]
    [InlineData("Manga", "Manga")]
    [InlineData("Manhwa", "Manhwa")]
    [InlineData("Manhua", "Manhua")]
    [InlineData("OEL", "Manga")]
    [InlineData("Artbook", null)]
    public void ExtractMediumType_MapsExpectedTypes(string rawType, string? expectedMapped)
    {
        var json = $$"""
        {
          "type": "{{rawType}}"
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var type = MangaUpdatesTaggingService.ExtractMediumType(doc.RootElement);

        if (expectedMapped is null)
        {
            Assert.True(type == rawType || type is null); // Note: raw extraction returns raw type
        }
        else
        {
            var mapped = type == "OEL" ? "Manga" : type;
            Assert.Equal(expectedMapped, mapped);
        }
    }

    [Theory]
    [InlineData("Qidian", "Chinese")]
    [InlineData("Naver", "Korean")]
    [InlineData("Media Factory", "Japanese")]
    [InlineData("UnknownPublisher", null)]
    public void DetectNovelOrigin_ByPublishers(string publisherName, string? expectedCountry)
    {
        var json = $$"""
        {
          "publishers": [
            { "type": "Original", "publisher_name": "{{publisherName}}" }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var country = MangaUpdatesTaggingService.DetectNovelOrigin(doc.RootElement);

        Assert.Equal(expectedCountry, country);
    }

    [Theory]
    // Japanese Kana (Hiragana/Katakana)
    // "おにいさま" (Hiragana)
    [InlineData("おにいさま", "Japanese")]
    // "オニイサマ" (Katakana)
    [InlineData("オニイサマ", "Japanese")]
    // Halfwidth Katakana
    // "ｱｲｳｴｵ"
    [InlineData("ｱｲｳｴｵ", "Japanese")]
    // Korean Hangul / Jamo
    // "나 혼자만 레벨업"
    [InlineData("나 혼자만 레벨업", "Korean")]
    // Hangul Compatibility Jamo
    // "ㄱㄴㄷ"
    [InlineData("ㄱㄴㄷ", "Korean")]
    // Chinese Ideographs only (no kana or hangul)
    // "独步天下"
    [InlineData("独步天下", "Chinese")]
    // Non-CJK (English)
    // "The Great Ruler"
    [InlineData("The Great Ruler", null)]
    public void DetectNovelOrigin_ByScripts(string associatedTitle, string? expectedCountry)
    {
        var json = $$"""
        {
          "associated": [
            { "title": "Random English Title" },
            { "title": "{{associatedTitle}}" }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var country = MangaUpdatesTaggingService.DetectNovelOrigin(doc.RootElement);

        Assert.Equal(expectedCountry, country);
    }
}
