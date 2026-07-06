using System.Text.Json;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using Xunit;

namespace BookmarkManager.UnitTests;

public sealed class AnilistScheduleTests
{
    [Fact]
    public void ParseCandidates_MapsAllFieldsFromEachMediaEntry()
    {
        const string json = """
        {
          "data": {
            "Page": {
              "media": [
                {
                  "id": 21,
                  "title": { "romaji": "One Piece", "english": "One Piece" },
                  "coverImage": { "large": "https://example.com/cover.jpg" },
                  "status": "RELEASING"
                },
                {
                  "id": 99,
                  "title": { "romaji": "Naruto", "english": null },
                  "coverImage": { "large": null },
                  "status": "FINISHED"
                }
              ]
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var candidates = AnilistTaggingService.ParseCandidates(doc.RootElement);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(21, candidates[0].AniListId);
        Assert.Equal("One Piece", candidates[0].RomajiTitle);
        Assert.Equal("One Piece", candidates[0].EnglishTitle);
        Assert.Equal("https://example.com/cover.jpg", candidates[0].CoverImageUrl);
        Assert.Equal("RELEASING", candidates[0].Status);

        Assert.Equal(99, candidates[1].AniListId);
        Assert.Null(candidates[1].EnglishTitle);
        Assert.Null(candidates[1].CoverImageUrl);
    }

    [Fact]
    public void ParseCandidates_ReturnsEmptyWhenShapeIsUnexpected()
    {
        using var doc = JsonDocument.Parse("""{ "data": null }""");

        var candidates = AnilistTaggingService.ParseCandidates(doc.RootElement);

        Assert.Empty(candidates);
    }

    [Fact]
    public void ParseMediaScheduleNode_ExtractsEpisodeAndUnixAiringTime()
    {
        const string json = """
        {
          "data": {
            "Media": {
              "id": 21,
              "status": "RELEASING",
              "airingSchedule": {
                "nodes": [
                  { "episode": 1093, "airingAt": 1735689600 },
                  { "episode": 1094, "airingAt": 1736294400 }
                ]
              }
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = AnilistTaggingService.ParseMediaScheduleNode(doc.RootElement);

        Assert.NotNull(result);
        Assert.Equal("RELEASING", result!.Status);
        Assert.Equal(2, result.Episodes.Count);
        Assert.Equal(1093, result.Episodes[0].EpisodeNumber);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1735689600), result.Episodes[0].AiringAtUtc);
        Assert.Equal(1094, result.Episodes[1].EpisodeNumber);
        Assert.Null(result.SequelId);
    }

    [Fact]
    public void ParseMediaScheduleNode_ExtractsSequelId_PreferringAnUpcomingSeason()
    {
        // A finished season whose franchise has a still-airing next season: schedule resolution
        // must be able to follow this SEQUEL edge to surface the new season on the calendar.
        const string json = """
        {
          "data": {
            "Media": {
              "id": 166873,
              "status": "FINISHED",
              "title": { "romaji": "Mushoku Tensei II Part 2", "english": null },
              "airingSchedule": { "nodes": [] },
              "relations": {
                "edges": [
                  { "relationType": "PREQUEL", "node": { "id": 146065, "type": "ANIME", "status": "FINISHED" } },
                  { "relationType": "SEQUEL",  "node": { "id": 178789, "type": "ANIME", "status": "RELEASING" } }
                ]
              }
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = AnilistTaggingService.ParseMediaScheduleNode(doc.RootElement);

        Assert.NotNull(result);
        Assert.Empty(result!.Episodes);
        Assert.Equal(178789, result.SequelId);
    }

    [Fact]
    public void ParseMediaScheduleNode_IgnoresNonAnimeSequel()
    {
        // A manga/light-novel sequel relation must not be followed - the calendar only schedules anime.
        const string json = """
        {
          "data": {
            "Media": {
              "id": 100,
              "status": "FINISHED",
              "airingSchedule": { "nodes": [] },
              "relations": {
                "edges": [
                  { "relationType": "SEQUEL", "node": { "id": 200, "type": "MANGA", "status": "RELEASING" } }
                ]
              }
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = AnilistTaggingService.ParseMediaScheduleNode(doc.RootElement);

        Assert.NotNull(result);
        Assert.Null(result!.SequelId);
    }

    [Theory]
    [InlineData(
        "Watch My Status as an Assassin Obviously Exceeds the Hero's English Sub/Dub online Free on Aniwatch.to",
        "https://aniwatch.to/watch/my-status-as-an-assassin-123",
        "Watch My Status as an Assassin Obviously Exceeds the Hero's")]
    [InlineData(
        "Naruto Shippuden Episode 1 English Subbed/Dubbed",
        null,
        "Naruto Shippuden Episode 1")]
    public void StripStreamingSiteJunk_RemovesTrailingSubDubAndHostSuffix(string title, string? url, string expected)
    {
        // This only strips the "English Sub/Dub ... on <host>" tail - the leading "Watch"
        // and chapter markers like "Episode 1" are cleaned up downstream by
        // MediaTitleNormalizer.Normalize, which SearchCandidatesAsync runs next.
        var cleaned = AnilistTaggingService.StripStreamingSiteJunk(title, url);

        Assert.Equal(expected, cleaned);
    }

    [Fact]
    public void FullNormalizationPipeline_ProducesCleanAniListQuery_ForRunOnStreamingSiteTitle()
    {
        const string title = "Watch My Status as an Assassin Obviously Exceeds the Hero's English Sub/Dub online Free on Aniwatch.to";
        const string url = "https://aniwatch.to/watch/my-status-as-an-assassin-123";

        var preCleaned = AnilistTaggingService.StripStreamingSiteJunk(title, url);
        var normalized = MediaTitleNormalizer.Normalize(preCleaned, url, BookmarkTagDomain.Anime);
        var candidate = normalized.Candidates.FirstOrDefault()?.Query ?? preCleaned;

        Assert.Equal("My Status as an Assassin Obviously Exceeds the Hero's", candidate);
    }

    [Fact]
    public void ParseMediaScheduleNode_ReturnsEmptyWhenNoUpcomingEpisodes()
    {
        const string json = """
        {
          "data": {
            "Media": {
              "id": 21,
              "status": "FINISHED",
              "airingSchedule": { "nodes": [] }
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(json);

        var result = AnilistTaggingService.ParseMediaScheduleNode(doc.RootElement);

        Assert.NotNull(result);
        Assert.Equal("FINISHED", result!.Status);
        Assert.Empty(result.Episodes);
        Assert.Null(result.SequelId);
    }
}
