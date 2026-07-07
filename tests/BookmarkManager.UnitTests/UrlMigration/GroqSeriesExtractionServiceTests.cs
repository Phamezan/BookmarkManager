using System.Net;
using System.Net.Http.Headers;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.UrlMigration;

public sealed class GroqSeriesExtractionServiceTests
{
    [Fact]
    public async Task ExtractBatchAsync_ValidGroqResponse_ReturnsParsedResultsInOrder()
    {
        var responseJson = """
            {
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "[{\"id\": 0, \"series\": \"Solo Leveling\", \"chapter\": \"110\", \"mediaType\": \"manhwa\"}, {\"id\": 1, \"series\": \"Omniscient Reader\", \"chapter\": \"112.5\", \"mediaType\": \"webnovel\"}]"
                        }
                    }
                ]
            }
            """;

        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) }));

        var service = CreateService(handler, out _);

        var items = new[]
        {
            new SeriesExtractionRequestItem("Solo Leveling - Chapter 110", "https://flamecomics.xyz/solo-leveling/chapter-110", null),
            new SeriesExtractionRequestItem("Omniscient Reader ch 112.5", "https://reaperscans.to/omniscient-reader/chapter-112.5", null)
        };

        var results = await service.ExtractBatchAsync(items, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("Solo Leveling", results[0].SeriesName);
        Assert.Equal("110", results[0].ChapterNumber);
        Assert.Equal("manhwa", results[0].MediaType);
        Assert.False(results[0].UsedFallback);

        Assert.Equal("Omniscient Reader", results[1].SeriesName);
        Assert.Equal("112.5", results[1].ChapterNumber);
        Assert.Equal("webnovel", results[1].MediaType);
        Assert.False(results[1].UsedFallback);
    }

    [Fact]
    public async Task ExtractBatchAsync_ResponseWrappedInCodeFences_StripsFencesAndParses()
    {
        var responseJson = """
            {
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "```json\n[{\"id\": 0, \"series\": \"Solo Leveling\", \"chapter\": \"110\", \"mediaType\": \"manhwa\"}]\n```"
                        }
                    }
                ]
            }
            """;

        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) }));

        var service = CreateService(handler, out _);

        var items = new[] { new SeriesExtractionRequestItem("Solo Leveling - Chapter 110", "https://flamecomics.xyz/solo-leveling/chapter-110", null) };
        var results = await service.ExtractBatchAsync(items, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Solo Leveling", results[0].SeriesName);
        Assert.False(results[0].UsedFallback);
    }

    [Fact]
    public async Task ExtractBatchAsync_MalformedJson_FallsBackForEveryItemInBatch()
    {
        var responseJson = """
            {
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "this is not json at all { [ }"
                        }
                    }
                ]
            }
            """;

        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) }));

        var service = CreateService(handler, out _);

        var items = new[]
        {
            new SeriesExtractionRequestItem("Solo Leveling - Chapter 110", "https://flamecomics.xyz/solo-leveling/chapter-110", null),
            new SeriesExtractionRequestItem("Omniscient Reader ch 112.5", "https://reaperscans.to/omniscient-reader/chapter-112.5", null)
        };

        var results = await service.ExtractBatchAsync(items, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.UsedFallback));
        Assert.Equal("110", results[0].ChapterNumber);
        Assert.Equal("112.5", results[1].ChapterNumber);
    }

    [Fact]
    public async Task ExtractBatchAsync_IdMismatch_FallsBackOnlyForTheAffectedItem()
    {
        // Response returns a valid result for id 0, but the second entry references an id that
        // doesn't exist in the request (id mismatch) - item 1 must fall back individually while
        // item 0's valid Groq result is preserved.
        var responseJson = """
            {
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "[{\"id\": 0, \"series\": \"Solo Leveling\", \"chapter\": \"110\", \"mediaType\": \"manhwa\"}, {\"id\": 99, \"series\": \"Bogus\", \"chapter\": \"1\", \"mediaType\": \"manga\"}]"
                        }
                    }
                ]
            }
            """;

        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) }));

        var service = CreateService(handler, out _);

        var items = new[]
        {
            new SeriesExtractionRequestItem("Solo Leveling - Chapter 110", "https://flamecomics.xyz/solo-leveling/chapter-110", null),
            new SeriesExtractionRequestItem("Omniscient Reader ch 112.5", "https://reaperscans.to/omniscient-reader/chapter-112.5", null)
        };

        var results = await service.ExtractBatchAsync(items, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.False(results[0].UsedFallback);
        Assert.Equal("Solo Leveling", results[0].SeriesName);

        Assert.True(results[1].UsedFallback);
        Assert.Equal("112.5", results[1].ChapterNumber);
    }

    [Fact]
    public async Task ExtractBatchAsync_DuplicateIdsInResponse_SecondDuplicateIgnored()
    {
        var responseJson = """
            {
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "[{\"id\": 0, \"series\": \"Solo Leveling\", \"chapter\": \"110\", \"mediaType\": \"manhwa\"}, {\"id\": 0, \"series\": \"Duplicate\", \"chapter\": \"1\", \"mediaType\": \"manga\"}]"
                        }
                    }
                ]
            }
            """;

        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) }));

        var service = CreateService(handler, out _);
        var items = new[] { new SeriesExtractionRequestItem("Solo Leveling - Chapter 110", "https://flamecomics.xyz/solo-leveling/chapter-110", null) };

        var results = await service.ExtractBatchAsync(items, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("Solo Leveling", results[0].SeriesName);
        Assert.False(results[0].UsedFallback);
    }

    [Fact]
    public async Task ExtractBatchAsync_MissingApiKey_FallsBackForEveryItem()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var settings = new AiTaggingSettingsDto { GroqApiKey = "" };
        var service = CreateService(handler, settings);

        var items = new[] { new SeriesExtractionRequestItem("Solo Leveling - Chapter 110", "https://flamecomics.xyz/solo-leveling/chapter-110", null) };
        var results = await service.ExtractBatchAsync(items, CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].UsedFallback);
        Assert.Equal("110", results[0].ChapterNumber);
    }

    [Fact]
    public async Task ExtractBatchAsync_RateLimited_FallsBackForEveryItem()
    {
        var handler = new MockHttpMessageHandler(_ =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return Task.FromResult(res);
        });

        var service = CreateService(handler, out _);
        var items = new[] { new SeriesExtractionRequestItem("Solo Leveling - Chapter 110", "https://flamecomics.xyz/solo-leveling/chapter-110", null) };
        var results = await service.ExtractBatchAsync(items, CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].UsedFallback);
    }

    [Fact]
    public async Task ExtractAsync_SingleItem_DelegatesToBatch()
    {
        var responseJson = """
            {
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "[{\"id\": 0, \"series\": \"Solo Leveling\", \"chapter\": \"110\", \"mediaType\": \"manhwa\"}]"
                        }
                    }
                ]
            }
            """;

        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) }));

        var service = CreateService(handler, out _);
        var result = await service.ExtractAsync("Solo Leveling - Chapter 110", "https://flamecomics.xyz/solo-leveling/chapter-110", null, CancellationToken.None);

        Assert.Equal("Solo Leveling", result.SeriesName);
        Assert.Equal("110", result.ChapterNumber);
        Assert.False(result.UsedFallback);
    }

    private static GroqSeriesExtractionService CreateService(HttpMessageHandler handler, out AiTaggingSettingsDto settingsDto)
    {
        settingsDto = new AiTaggingSettingsDto
        {
            GroqApiKey = "test-key",
            GroqModel = "llama-3.3-70b-versatile",
            GroqBaseUrl = "https://api.groq.com/openai/v1",
            GroqRequestsPerMinute = 6000
        };
        return CreateService(handler, settingsDto);
    }

    private static GroqSeriesExtractionService CreateService(HttpMessageHandler handler, AiTaggingSettingsDto settingsDto)
    {
        var httpClient = new HttpClient(handler);
        var factory = new SingleClientFactory(httpClient);
        var settingsService = new InMemoryAiTaggingSettingsService(settingsDto);
        return new GroqSeriesExtractionService(factory, settingsService, NullLogger<GroqSeriesExtractionService>.Instance);
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responseFactory(request);
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
