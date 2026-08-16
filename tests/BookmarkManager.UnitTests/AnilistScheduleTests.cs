using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // AniList's search is a strict phrase matcher, so long slug-derived queries often return zero
    // candidates. The fallback pass retries with shortened prefixes and title-derived variants -
    // these tests pin down that recovery and its franchise disambiguation.
    [Fact]
    public async Task FindBestMatchesBatchAsync_FallsBackToTitleDerivedQueryWhenSlugQueryHasNoCandidates()
    {
        var service = CreateCandidateService(new Dictionary<string, List<(int Id, string Romaji, string? English)>>
        {
            ["fate strange fake"] = [(166617, "Fate/strange Fake", "Fate/strange Fake")],
        });
        var id = Guid.NewGuid();

        var results = await service.FindBestMatchesBatchAsync(
            [(id, "Watch Fate/strange Fake English Sub/Dub online Free on Aniwatch.to",
                "https://aniwatchtv.to/watch/fatestrange-fake-19436?ep=169202")],
            CancellationToken.None);

        Assert.False(results[id].Unavailable);
        Assert.Equal(166617, results[id].Match?.AniListId);
    }

    [Fact]
    public async Task FindBestMatchesBatchAsync_FallbackPrefersMatchingSeasonNumber()
    {
        var service = CreateCandidateService(new Dictionary<string, List<(int Id, string Romaji, string? English)>>
        {
            ["konosuba"] =
            [
                (21202, "Kono Subarashii Sekai ni Shukufuku wo!", "KONOSUBA -God's blessing on this wonderful world!"),
                (136804, "Kono Subarashii Sekai ni Shukufuku wo! 3", "KONOSUBA -God's blessing on this wonderful world! 3"),
            ],
        });
        var id = Guid.NewGuid();

        var results = await service.FindBestMatchesBatchAsync(
            [(id, "Watch KonoSuba: God's Blessing on This Wonderful World! 3 English Sub/Dub online Free on Aniwatch.to",
                "https://aniwatchtv.to/watch/konosuba-gods-blessing-on-this-wonderful-world-3-49100?ep=123214")],
            CancellationToken.None);

        Assert.Equal(136804, results[id].Match?.AniListId);
    }

    [Fact]
    public async Task FindBestMatchesBatchAsync_FallbackPenalizesContradictingPartNumber()
    {
        var service = CreateCandidateService(new Dictionary<string, List<(int Id, string Romaji, string? English)>>
        {
            ["demon slayer kimetsu no yaiba"] =
            [
                (101922, "Kimetsu no Yaiba", "Demon Slayer: Kimetsu no Yaiba"),
                (178788, "Kimetsu no Yaiba: Mugenjou-hen Movie 1 - Akaza Sairai", "Demon Slayer: Kimetsu no Yaiba Infinity Castle"),
                (195200, "Kimetsu no Yaiba: Mugenjou-hen Movie 2", "Demon Slayer: Kimetsu no Yaiba Infinity Castle Part 2"),
            ],
        });
        var id = Guid.NewGuid();

        var results = await service.FindBestMatchesBatchAsync(
            [(id, "Watch Demon Slayer: Kimetsu no Yaiba - The Movie: Infinity Castle - Part 1: Akaza Returns English Sub/Dub online Free on Aniwatch.to",
                "https://aniwatchtv.to/watch/demon-slayer-kimetsu-no-yaiba-the-movie-infinity-castle-part-1-akaza-returns-19722?ep=142018")],
            CancellationToken.None);

        Assert.Equal(178788, results[id].Match?.AniListId);
    }

    [Fact]
    public async Task FindBestMatchesBatchAsync_LeavesItemUnmatchedWhenNoVariantHasCandidates()
    {
        var service = CreateCandidateService(new Dictionary<string, List<(int Id, string Romaji, string? English)>>());
        var id = Guid.NewGuid();

        var results = await service.FindBestMatchesBatchAsync(
            [(id, "Watch Re:CREATORS English Sub/Dub online Free on Aniwatch.to",
                "https://aniwatchtv.to/watch/recreators-1296?ep=18880")],
            CancellationToken.None);

        Assert.False(results[id].Unavailable);
        Assert.Null(results[id].Match);
    }

    // Answers the batched GraphQL candidate query: variables s0..sN carry the search strings,
    // aliases t0..tN carry the per-query Page results. Queries not present in the map return an
    // empty media list, mimicking AniList's zero-results phrase-matching misses.
    private static AnilistTaggingService CreateCandidateService(
        IReadOnlyDictionary<string, List<(int Id, string Romaji, string? English)>> mediaByQuery)
    {
        var handler = new UrlMigration.TestDoubles.MockHttpMessageHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            var data = new JsonObject();
            foreach (var variable in doc.RootElement.GetProperty("variables").EnumerateObject())
            {
                var alias = "t" + variable.Name[1..];
                var query = variable.Value.GetString() ?? string.Empty;

                var media = new JsonArray();
                if (mediaByQuery.TryGetValue(query, out var entries))
                {
                    foreach (var (mediaId, romaji, english) in entries)
                    {
                        media.Add(new JsonObject
                        {
                            ["id"] = mediaId,
                            ["title"] = new JsonObject { ["romaji"] = romaji, ["english"] = english },
                            ["coverImage"] = new JsonObject { ["large"] = null },
                            ["status"] = "FINISHED"
                        });
                    }
                }

                data[alias] = new JsonObject { ["media"] = media };
            }

            var payload = new JsonObject { ["data"] = data }.ToJsonString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        });

        return new AnilistTaggingService(
            new UrlMigration.TestDoubles.SingleClientFactory(new HttpClient(handler)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AnilistTaggingService>.Instance);
    }
}
