using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests;

public sealed class OpenRouterSeriesIdentificationClientTests
{
    [Fact]
    public async Task IdentifyAsync_Success_ReturnsResponse()
    {
        var responseJson = """
            {
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "{ \"items\": [] }"
                        }
                    }
                ]
            }
            """;

        var handler = new MockHttpMessageHandler(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson) };
        });

        var client = CreateClient(handler, out _, out _);
        var request = new AiSeriesIdentifyRequest("Instructions", new List<AiSeriesIdentifyRequestItem>());
        var response = await client.IdentifyAsync(request, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("{ \"items\": [] }", response.Json);
        Assert.Null(response.RateLimit);
    }

    [Fact]
    public async Task IdentifyAsync_RateLimitWithRetryAfter_ReturnsRateLimitAndRecordsInThrottle()
    {
        var handler = new MockHttpMessageHandler(request =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            res.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return Task.FromResult(res);
        });

        var client = CreateClient(handler, out _, out var throttle);
        var request = new AiSeriesIdentifyRequest("Instructions", new List<AiSeriesIdentifyRequestItem>());
        var response = await client.IdentifyAsync(request, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.RateLimit?.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(5), response.RateLimit?.RetryAfter);
        Assert.Contains("rate limit", response.RateLimit?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IdentifyAsync_RateLimitWithoutRetryAfter_ReturnsRateLimitAndRecordsDefaultInThrottle()
    {
        var handler = new MockHttpMessageHandler(request =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        });

        var client = CreateClient(handler, out _, out var throttle);
        var request = new AiSeriesIdentifyRequest("Instructions", new List<AiSeriesIdentifyRequestItem>());
        var response = await client.IdentifyAsync(request, CancellationToken.None);

        Assert.NotNull(response);
        Assert.True(response.RateLimit?.IsRateLimited);
        Assert.Equal(TimeSpan.FromSeconds(60), response.RateLimit?.RetryAfter);
    }

    [Fact]
    public async Task IdentifyAsync_PaymentRequired_ThrowsCreditException()
    {
        var handler = new MockHttpMessageHandler(request =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired));
        });

        var client = CreateClient(handler, out _, out _);
        var request = new AiSeriesIdentifyRequest("Instructions", new List<AiSeriesIdentifyRequestItem>());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.IdentifyAsync(request, CancellationToken.None));
        Assert.Contains("credit/balance", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.PaymentRequired, ex.StatusCode);
    }

    [Fact]
    public async Task IdentifyAsync_UnauthorizedOrForbidden_ThrowsConfigException()
    {
        var handler1 = new MockHttpMessageHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client1 = CreateClient(handler1, out _, out _);
        var request = new AiSeriesIdentifyRequest("Instructions", new List<AiSeriesIdentifyRequestItem>());

        var ex1 = await Assert.ThrowsAsync<HttpRequestException>(() => client1.IdentifyAsync(request, CancellationToken.None));
        Assert.Contains("key/config", ex1.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, ex1.StatusCode);

        var handler2 = new MockHttpMessageHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var client2 = CreateClient(handler2, out _, out _);
        var ex2 = await Assert.ThrowsAsync<HttpRequestException>(() => client2.IdentifyAsync(request, CancellationToken.None));
        Assert.Contains("key/config", ex2.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, ex2.StatusCode);
    }

    [Fact]
    public async Task IdentifyAsync_DisabledSettings_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpMessageHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var settings = new AiTaggingSettingsDto
        {
            Enabled = false,
            ApiKey = "test-key"
        };
        var client = CreateClient(handler, settings, out _);
        var request = new AiSeriesIdentifyRequest("Instructions", new List<AiSeriesIdentifyRequestItem>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.IdentifyAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task IdentifyAsync_MissingApiKey_ThrowsInvalidOperationException()
    {
        var handler = new MockHttpMessageHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var settings = new AiTaggingSettingsDto
        {
            Enabled = true,
            ApiKey = ""
        };
        var client = CreateClient(handler, settings, out _);
        var request = new AiSeriesIdentifyRequest("Instructions", new List<AiSeriesIdentifyRequestItem>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.IdentifyAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task AiRequestThrottle_PacesRequests()
    {
        var throttle = new AiRequestThrottle();
        var startTime = DateTime.UtcNow;

        // First call should complete immediately
        await throttle.AwaitThrottleAsync(60, CancellationToken.None);
        var firstTime = DateTime.UtcNow;

        // Second call should be delayed by 1 second (since RPM is 60, delay = ceil(60/60) = 1s)
        await throttle.AwaitThrottleAsync(60, CancellationToken.None);
        var secondTime = DateTime.UtcNow;

        var duration = secondTime - firstTime;
        Assert.True(duration >= TimeSpan.FromMilliseconds(800), $"Expected at least ~1s pacing delay, got {duration.TotalMilliseconds}ms");
    }

    private static OpenRouterSeriesIdentificationClient CreateClient(
        HttpMessageHandler handler,
        out AiTaggingSettingsDto settingsDto,
        out AiRequestThrottle throttle)
    {
        settingsDto = new AiTaggingSettingsDto
        {
            Enabled = true,
            ApiKey = "test-key",
            Model = "google/gemini-2.5-flash:free",
            BaseUrl = "https://openrouter.ai/api/v1",
            RequestsPerMinute = 60,
            PreferredBatchSize = 25
        };
        return CreateClient(handler, settingsDto, out throttle);
    }

    private static OpenRouterSeriesIdentificationClient CreateClient(
        HttpMessageHandler handler,
        AiTaggingSettingsDto settingsDto,
        out AiRequestThrottle throttle)
    {
        var httpClient = new HttpClient(handler);
        var factory = new SingleClientFactory(httpClient);
        var settingsService = new InMemoryAiTaggingSettingsService(settingsDto);
        throttle = new AiRequestThrottle();
        return new OpenRouterSeriesIdentificationClient(
            factory,
            settingsService,
            throttle,
            NullLogger<OpenRouterSeriesIdentificationClient>.Instance);
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory) : HttpMessageValueHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responseFactory(request);
    }

    // Abstract HttpMessageValueHandler to avoid conflict / simplify implementation
    private class HttpMessageValueHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
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
