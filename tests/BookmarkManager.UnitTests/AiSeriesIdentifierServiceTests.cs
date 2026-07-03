using System.Net;
using System.Text.Json;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests;

public sealed class AiSeriesIdentifierServiceTests
{
    [Fact]
    public void BuildPayloads_UsesOriginalTitleUrlHostFolderPathAndDomainHint()
    {
        var novelId = Guid.NewGuid();
        var mangaId = Guid.NewGuid();
        var animeId = Guid.NewGuid();
        var service = new AiSeriesIdentifierService();

        var payloads = service.BuildPayloads(
            [
                new AiSeriesIdentifyCandidate(
                    novelId,
                    "Lightnovels.me - read A Monster Who Levels Up Chapter 48 Online",
                    "https://lightnovels.me/novel/a-monster-who-levels-up/chapter-48",
                    "Media / Light Novels"),
                new AiSeriesIdentifyCandidate(
                    mangaId,
                    "Solo Leveling Chapter 12 - Asura Scans",
                    "https://asuracomic.net/series/solo-leveling/chapter-12",
                    "Media / Manhwa"),
                new AiSeriesIdentifyCandidate(
                    animeId,
                    "One Piece Episode 1092",
                    "https://crunchyroll.com/watch/one-piece-1092",
                    "Media / Anime")
            ]);

        Assert.Collection(
            payloads,
            novel =>
            {
                Assert.Equal(novelId, novel.Id);
                Assert.Equal("Lightnovels.me - read A Monster Who Levels Up Chapter 48 Online", novel.Title);
                Assert.Equal("lightnovels.me", novel.UrlHost);
                Assert.Equal("Media / Light Novels", novel.FolderPath);
                Assert.Equal(BookmarkTagDomainDto.Novel, novel.DomainHint);
            },
            manga =>
            {
                Assert.Equal(mangaId, manga.Id);
                Assert.Equal("Solo Leveling Chapter 12 - Asura Scans", manga.Title);
                Assert.Equal("asuracomic.net", manga.UrlHost);
                Assert.Equal("Media / Manhwa", manga.FolderPath);
                Assert.Equal(BookmarkTagDomainDto.Manga, manga.DomainHint);
            },
            anime =>
            {
                Assert.Equal(animeId, anime.Id);
                Assert.Equal("One Piece Episode 1092", anime.Title);
                Assert.Equal("crunchyroll.com", anime.UrlHost);
                Assert.Equal("Media / Anime", anime.FolderPath);
                Assert.Equal(BookmarkTagDomainDto.Anime, anime.DomainHint);
            });
    }

    [Fact]
    public async Task IdentifyAsync_SendsJsonOnlyNoTagsPromptAndReturnsValidIdentification()
    {
        var id = Guid.NewGuid();
        string? requestJson = null;
        var handler = new QueueHttpMessageHandler(_ => $$"""
            {
              "items": [
                {
                  "id": "{{id}}",
                  "canonicalTitle": "A Monster Who Levels Up",
                  "confidence": 0.91,
                  "sourceHint": "Novel"
                }
              ]
            }
            """);
        var service = CreateService(handler);
        handler.OnRequest = async request => requestJson = await request.Content!.ReadAsStringAsync();

        var summary = await service.IdentifyAsync(
            [new AiSeriesIdentifyCandidate(id, "Lightnovels.me - read A Monster Who Levels Up Chapter 48 Online", "https://lightnovels.me/novel/a-monster-who-levels-up/chapter-48", "Media / Light Novels")],
            CancellationToken.None);

        var result = Assert.Single(summary.Items);
        Assert.Equal(id, result.Id);
        Assert.Equal("A Monster Who Levels Up", result.CanonicalTitle);
        Assert.Equal(0.91, result.Confidence);
        Assert.Equal(AiSeriesSourceHint.Novel, result.SourceHint);
        Assert.Equal(0, summary.FailedChunks);
        Assert.Contains("Return JSON only", requestJson);
        Assert.Contains("exactly one result for each input id", requestJson);
        Assert.Contains("Do not generate tags", requestJson);
        Assert.Contains("canonicalTitle", requestJson);
        Assert.Contains("confidence", requestJson);
        Assert.Contains("sourceHint", requestJson);
    }

    [Fact]
    public async Task IdentifyAsync_EmptyCanonicalTitleSkipsItemWithoutFailingChunk()
    {
        var id = Guid.NewGuid();
        var handler = new QueueHttpMessageHandler(_ => $$"""
            {
              "items": [
                { "id": "{{id}}", "canonicalTitle": "", "confidence": 0.2, "sourceHint": "Unknown" }
              ]
            }
            """);
        var service = CreateService(handler);

        var summary = await service.IdentifyAsync([new AiSeriesIdentifyCandidate(id, "ambiguous", null, null)], CancellationToken.None);

        Assert.Empty(summary.Items);
        Assert.Equal(0, summary.FailedChunks);
        Assert.Contains(summary.Messages, message => message.Contains("AI identification chunk 1/1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IdentifyAsync_WithGeminiSettingsPostsToGeminiGenerateContentAndParsesJsonText()
    {
        var id = Guid.NewGuid();
        string? requestedUri = null;
        string? requestJson = null;
        var httpClient = new HttpClient(new GeminiHttpMessageHandler(async request =>
        {
            requestedUri = request.RequestUri!.ToString();
            requestJson = await request.Content!.ReadAsStringAsync();
            return $$"""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          { "text": "{ \"items\": [ { \"id\": \"{{id}}\", \"canonicalTitle\": \"One Piece\", \"confidence\": 0.96, \"sourceHint\": \"Anime\" } ] }" }
                        ]
                      }
                    }
                  ]
                }
                """;
        }));
        var settings = new InMemoryAiTaggingSettingsService(new AiTaggingSettingsDto
        {
            Enabled = true,
            Model = "gemini-2.5-flash",
            ApiKey = "gemini-key"
        });
        var service = new AiSeriesIdentifierService(new SingleClientFactory(httpClient), settings);

        var summary = await service.IdentifyAsync(
            [new AiSeriesIdentifyCandidate(id, "One Piece Episode 1092", "https://crunchyroll.com/watch/one-piece", "Anime")],
            CancellationToken.None);

        var item = Assert.Single(summary.Items);
        Assert.Equal("One Piece", item.CanonicalTitle);
        Assert.Equal(AiSeriesSourceHint.Anime, item.SourceHint);
        Assert.Contains("models/gemini-2.5-flash:generateContent", requestedUri);
        Assert.Contains("key=gemini-key", requestedUri);
        Assert.Contains("Return JSON only", requestJson);
        Assert.Contains("One Piece Episode 1092", requestJson);
        Assert.Contains("responseMimeType", requestJson);
    }

    [Fact]
    public async Task IdentifyAsync_InvalidIdItemDoesNotDiscardValidItems()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var handler = new QueueHttpMessageHandler(_ => $$"""
            {
              "items": [
                { "id": "{{firstId}}", "canonicalTitle": "Solo Leveling", "confidence": 0.82, "sourceHint": "Manhwa" },
                { "id": "not-a-guid", "canonicalTitle": "Bad", "confidence": 0.90, "sourceHint": "Unknown" },
                { "id": "{{secondId}}", "canonicalTitle": "One Piece", "confidence": 0.94, "sourceHint": "Anime" }
              ]
            }
            """);
        var service = CreateService(handler);

        var summary = await service.IdentifyAsync(
            [
                new AiSeriesIdentifyCandidate(firstId, "Solo Leveling Chapter 12", null, "Media / Manhwa"),
                new AiSeriesIdentifyCandidate(secondId, "One Piece Episode 1092", null, "Media / Anime")
            ],
            CancellationToken.None);

        Assert.Equal(0, summary.FailedChunks);
        Assert.Collection(
            summary.Items,
            item => Assert.Equal("Solo Leveling", item.CanonicalTitle),
            item => Assert.Equal("One Piece", item.CanonicalTitle));
        Assert.Contains(summary.Messages, message => message.Contains("invalid id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IdentifyAsync_SplitsLargeCandidateSetsIntoSmallerRequests()
    {
        var candidates = Enumerable.Range(1, 121)
            .Select(index => new AiSeriesIdentifyCandidate(Guid.NewGuid(), $"Manga Series {index}", null, "Manga"))
            .ToList();
        var requestSizes = new List<int>();
        var handler = new QueueHttpMessageHandler(request =>
        {
            var requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(requestJson);
            var ids = document.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid())
                .ToList();
            requestSizes.Add(ids.Count);

            var items = ids.Select(id => new
            {
                id,
                canonicalTitle = $"Series {id.ToString()[..8]}",
                confidence = 0.9,
                sourceHint = "Manga"
            });
            return JsonSerializer.Serialize(new { items });
        });
        var service = CreateService(handler);

        var summary = await service.IdentifyAsync(candidates, CancellationToken.None);

        Assert.Equal(121, summary.Items.Count);
        Assert.Equal([50, 50, 21], requestSizes);
        Assert.Equal(0, summary.FailedChunks);
        Assert.Contains(summary.Messages, message => message.Contains("AI identification chunk 1/3", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(summary.Messages, message => message.Contains("AI identification chunk 3/3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IdentifyAsync_RetriesTransientServiceUnavailableResponse()
    {
        var id = Guid.NewGuid();
        var handler = new QueueHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "items": [
                        { "id": "{{id}}", "canonicalTitle": "One Piece", "confidence": 0.94, "sourceHint": "Anime" }
                      ]
                    }
                    """)
            });
        var service = CreateService(handler);

        var summary = await service.IdentifyAsync(
            [new AiSeriesIdentifyCandidate(id, "One Piece Episode 1092", null, "Anime")],
            CancellationToken.None);

        var item = Assert.Single(summary.Items);
        Assert.Equal("One Piece", item.CanonicalTitle);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(0, summary.FailedChunks);
        Assert.Contains(summary.Messages, message => message.Contains("retrying", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(InvalidResponseCases))]
    public async Task IdentifyAsync_RetriesInvalidChunkOnceThenReturnsOnlyRetryOutput(string invalidJson)
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var handler = new QueueHttpMessageHandler(
            _ => invalidJson.Replace("FIRST_ID", firstId.ToString()).Replace("SECOND_ID", secondId.ToString()),
            _ => $$"""
                {
                  "items": [
                    { "id": "{{firstId}}", "canonicalTitle": "Solo Leveling", "confidence": 0.82, "sourceHint": "Manhwa" },
                    { "id": "{{secondId}}", "canonicalTitle": "One Piece", "confidence": 0.94, "sourceHint": "Anime" }
                  ]
                }
                """);
        var service = CreateService(handler);

        var summary = await service.IdentifyAsync(
            [
                new AiSeriesIdentifyCandidate(firstId, "Solo Leveling Chapter 12 - Asura Scans", "https://asuracomic.net/series/solo-leveling/chapter-12", "Media / Manhwa"),
                new AiSeriesIdentifyCandidate(secondId, "One Piece Episode 1092", "https://crunchyroll.com/watch/one-piece-1092", "Media / Anime")
            ],
            CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(0, summary.FailedChunks);
        Assert.Collection(
            summary.Items,
            item => Assert.Equal("Solo Leveling", item.CanonicalTitle),
            item => Assert.Equal("One Piece", item.CanonicalTitle));
    }

    [Fact]
    public async Task IdentifyAsync_SkipsChunkWithoutPartialOutputWhenRetryIsStillInvalid()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var missingSecondIdResponse = $$"""
            {
              "items": [
                { "id": "{{firstId}}", "canonicalTitle": "Solo Leveling", "confidence": 0.82, "sourceHint": "Manhwa" }
              ]
            }
            """;
        var handler = new QueueHttpMessageHandler(_ => missingSecondIdResponse, _ => missingSecondIdResponse);
        var service = CreateService(handler);

        var summary = await service.IdentifyAsync(
            [
                new AiSeriesIdentifyCandidate(firstId, "Solo Leveling Chapter 12 - Asura Scans", null, null),
                new AiSeriesIdentifyCandidate(secondId, "One Piece Episode 1092", null, null)
            ],
            CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
        Assert.Empty(summary.Items);
        Assert.Equal(1, summary.FailedChunks);
        Assert.Contains(summary.Messages, message => message.Contains("invalid AI identification response", StringComparison.OrdinalIgnoreCase));
    }

    public static TheoryData<string> InvalidResponseCases()
        => new()
        {
            // missing ID
            """
            { "items": [ { "id": "FIRST_ID", "canonicalTitle": "Solo Leveling", "confidence": 0.8, "sourceHint": "Manhwa" } ] }
            """,
            // duplicate ID
            """
            { "items": [ { "id": "FIRST_ID", "canonicalTitle": "Solo Leveling", "confidence": 0.8, "sourceHint": "Manhwa" }, { "id": "FIRST_ID", "canonicalTitle": "Solo Leveling", "confidence": 0.9, "sourceHint": "Manhwa" } ] }
            """,
            // extra ID
            """
            { "items": [ { "id": "FIRST_ID", "canonicalTitle": "Solo Leveling", "confidence": 0.8, "sourceHint": "Manhwa" }, { "id": "SECOND_ID", "canonicalTitle": "One Piece", "confidence": 0.9, "sourceHint": "Anime" }, { "id": "00000000-0000-0000-0000-000000000001", "canonicalTitle": "Extra", "confidence": 0.9, "sourceHint": "Unknown" } ] }
            """,
            // invalid confidence
            """
            { "items": [ { "id": "FIRST_ID", "canonicalTitle": "Solo Leveling", "confidence": 1.1, "sourceHint": "Manhwa" }, { "id": "SECOND_ID", "canonicalTitle": "One Piece", "confidence": 0.9, "sourceHint": "Anime" } ] }
            """,
            // invalid JSON
            """
            { "items": [
            """
        };

    private static AiSeriesIdentifierService CreateService(QueueHttpMessageHandler handler)
        => new(new HttpClient(handler), new Uri("https://ai.local/identify"));

    private sealed class QueueHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(responses);

        public QueueHttpMessageHandler(params Func<HttpRequestMessage, string>[] responses)
            : this(responses.Select<Func<HttpRequestMessage, string>, Func<HttpRequestMessage, HttpResponseMessage>>(response => request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(request))
            }).ToArray())
        {
        }

        public Func<HttpRequestMessage, Task>? OnRequest { get; set; }

        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (OnRequest is not null)
                await OnRequest(request);

            var responseFactory = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return responseFactory(request);
        }
    }

    private sealed class GeminiHttpMessageHandler(Func<HttpRequestMessage, Task<string>> responseFactory) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => new(HttpStatusCode.OK) { Content = new StringContent(await responseFactory(request)) };
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class InMemoryAiTaggingSettingsService(AiTaggingSettingsDto settings)
        : AiTaggingSettingsService(NullLogger<AiTaggingSettingsService>.Instance)
    {
        public override Task<AiTaggingSettingsDto> GetAsync(CancellationToken cancellationToken)
            => Task.FromResult(settings);
    }
}
