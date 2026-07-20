using System.Text.Json;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using Xunit;

namespace BookmarkManager.UnitTests;

public sealed class AnilistTaggingTests
{
    [Theory]
    [InlineData("One Piece - Episode 1092", "One Piece")]
    [InlineData("Jujutsu Kaisen - Chapter 245", "Jujutsu Kaisen")]
    [InlineData("[Season 3] Ep. 05 - The Return (5) | Kubera", "The Return")]
    [InlineData("990k Ex-Life Hunter - Chapter 41 - WEBTOON XYZ", "990k Ex-Life Hunter")]
    [InlineData("TFT ACADEMY | Home", "TFT ACADEMY")]
    [InlineData("Phamezan#NA1 - Set 16 Overview - LoLCHESS.GG", "Phamezan#NA1")]
    [InlineData("TFT Handbook - Robinsongz TFT Handbook", "TFT Handbook")]
    [InlineData("tft champ pool", "tft champ pool")]
    [InlineData("One Piece 1092 discussion", "One Piece 1092 discussion")]
    [InlineData("Watch MARRIAGETOXIN · Miruro - Episode 13", "MARRIAGETOXIN")]
    [InlineData("Watch Naruto · Crunchyroll - Episode 1", "Naruto")]
    public void CentralTitleNormalizer_StripsCommonJunkCorrectly(string title, string expected)
    {
        var cleaned = MediaTitleNormalizer.CleanTitle(title);
        Assert.Equal(expected, cleaned);
    }

    [Fact]
    public void ScoreCandidate_CalculatesExpectedSimilarity()
    {
        const string json = """
        {
          "title": {
            "romaji": "One Piece",
            "english": "One Piece",
            "native": "ワンピース"
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        // Exact match
        var score1 = AnilistTaggingService.ScoreCandidate(doc.RootElement, "One Piece");
        Assert.Equal(1.0, score1);

        // Close match
        var score2 = AnilistTaggingService.ScoreCandidate(doc.RootElement, "One Piece anime");
        Assert.True(score2 >= 0.55);

        // Bad match
        var score3 = AnilistTaggingService.ScoreCandidate(doc.RootElement, "Attack on Titan");
        Assert.True(score3 < 0.55);
    }

    [Fact]
    public void ProcessCandidates_SelectsBestAboveThreshold()
    {
        const string json = """
        {
          "data": {
            "Page": {
              "media": [
                {
                  "title": { "romaji": "Attack on Titan", "english": "Attack on Titan", "native": "" },
                  "genres": ["Action"],
                  "tags": [
                    { "name": "Gore", "rank": 90, "isMediaSpoiler": false, "isGeneralSpoiler": false }
                  ]
                },
                {
                  "title": { "romaji": "One Piece", "english": "One Piece", "native": "" },
                  "coverImage": { "extraLarge": "https://cdn/op-xl.jpg", "large": "https://cdn/op-l.jpg" },
                  "genres": ["Action", "Adventure"],
                  "tags": [
                    { "name": "Pirates", "rank": 95, "isMediaSpoiler": false, "isGeneralSpoiler": false }
                  ]
                }
              ]
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var (tags, wasRejected, rejectionReason, canonicalTitle, matchScore, coverImageUrl) = AnilistTaggingService.ProcessCandidates(doc.RootElement, "One Piece");

        Assert.False(wasRejected);
        Assert.Null(rejectionReason);
        Assert.NotNull(matchScore);
        Assert.Contains("Adventure", tags);
        Assert.Contains("Pirates", tags);
        Assert.Equal("One Piece", canonicalTitle);
        // Prefers the extraLarge poster from the winning candidate.
        Assert.Equal("https://cdn/op-xl.jpg", coverImageUrl);
    }

    [Fact]
    public void ProcessCandidates_PrefersEnglishOverRomajiForCanonicalTitle()
    {
        const string json = """
        {
          "data": {
            "Page": {
              "media": [
                {
                  "title": { "romaji": "Shingeki no Kyojin", "english": "Attack on Titan", "native": "進撃の巨人" },
                  "genres": ["Action"],
                  "tags": []
                }
              ]
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var (_, _, _, canonicalTitle, _, _) = AnilistTaggingService.ProcessCandidates(doc.RootElement, "Attack on Titan");

        Assert.Equal("Attack on Titan", canonicalTitle);
    }

    [Fact]
    public void ProcessCandidates_RejectsWhenAllBelowThreshold()
    {
        const string json = """
        {
          "data": {
            "Page": {
              "media": [
                {
                  "title": { "romaji": "Attack on Titan", "english": "Attack on Titan", "native": "" },
                  "genres": ["Action"],
                  "tags": []
                }
              ]
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var (tags, wasRejected, rejectionReason, canonicalTitle, matchScore, _) = AnilistTaggingService.ProcessCandidates(doc.RootElement, "One Piece");

        Assert.False(wasRejected);
        Assert.NotNull(rejectionReason);
        Assert.Null(matchScore);
        Assert.Empty(tags);
        Assert.Null(canonicalTitle);
    }
}
