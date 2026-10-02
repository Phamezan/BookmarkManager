using System.Net;
using System.Net.Http;
using System.Text;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using BookmarkManager.UnitTests.UrlMigration.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests;

public sealed class CompositeSeriesIdentificationClientTests
{
    [Fact]
    public async Task TestConnectionAsync_Gemini_ProbesStoredEndpointWithHeader()
    {
        string? requestedUri = null;
        string? apiKeyHeader = null;
        var handler = new MockHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri!.ToString();
            apiKeyHeader = request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        });
        var httpClient = new HttpClient(handler);
        var factory = new SingleClientFactory(httpClient);
        var settings = new InMemoryAiTaggingSettingsService(new AiTaggingSettingsDto());

        var composite = new CompositeSeriesIdentificationClient(
            new OpenRouterSeriesIdentificationClient(factory, settings, new AiRequestThrottle(), NullLogger<OpenRouterSeriesIdentificationClient>.Instance),
            new GroqSeriesIdentificationClient(factory, settings, NullLogger<GroqSeriesIdentificationClient>.Instance),
            settings,
            factory,
            NullLogger<CompositeSeriesIdentificationClient>.Instance);

        var result = await composite.TestConnectionAsync(new TestAiKeyRequest
        {
            Provider = "Gemini",
            SecretName = "GeminiApiKey",
            BaseUrl = "https://generativelanguage.example/v1beta",
            Model = "gemini-3.8-flash",
            ApiKey = "gem-key"
        }, default);

        Assert.True(result.Success);
        Assert.Equal("https://generativelanguage.example/v1beta/models", requestedUri);
        Assert.Equal("gem-key", apiKeyHeader);
        Assert.DoesNotContain("/chat/completions", requestedUri);
    }

    [Fact]
    public async Task TestConnectionAsync_Gemini_Unauthorized_ReportsFailure()
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }));
        var factory = new SingleClientFactory(new HttpClient(handler));
        var settings = new InMemoryAiTaggingSettingsService(new AiTaggingSettingsDto());

        var composite = new CompositeSeriesIdentificationClient(
            new OpenRouterSeriesIdentificationClient(factory, settings, new AiRequestThrottle(), NullLogger<OpenRouterSeriesIdentificationClient>.Instance),
            new GroqSeriesIdentificationClient(factory, settings, NullLogger<GroqSeriesIdentificationClient>.Instance),
            settings,
            factory,
            NullLogger<CompositeSeriesIdentificationClient>.Instance);

        var result = await composite.TestConnectionAsync(new TestAiKeyRequest
        {
            Provider = "Gemini",
            SecretName = "GeminiApiKey",
            BaseUrl = "https://generativelanguage.example/v1beta",
            ApiKey = "bad-key"
        }, default);

        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task TestConnectionAsync_Gemini_PaymentRequired_ShowsGoogleStatusAndMessage()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent(
                """{"error":{"code":402,"status":"PAYMENT_REQUIRED","message":"Prepayment credits are depleted."}}""",
                Encoding.UTF8,
                "application/json")
        }));
        var factory = new SingleClientFactory(new HttpClient(handler));
        var settings = new InMemoryAiTaggingSettingsService(new AiTaggingSettingsDto());

        var composite = new CompositeSeriesIdentificationClient(
            new OpenRouterSeriesIdentificationClient(factory, settings, new AiRequestThrottle(), NullLogger<OpenRouterSeriesIdentificationClient>.Instance),
            new GroqSeriesIdentificationClient(factory, settings, NullLogger<GroqSeriesIdentificationClient>.Instance),
            settings,
            factory,
            NullLogger<CompositeSeriesIdentificationClient>.Instance);

        var result = await composite.TestConnectionAsync(new TestAiKeyRequest
        {
            Provider = "Gemini",
            SecretName = "GeminiApiKey",
            BaseUrl = "https://generativelanguage.example/v1beta",
            ApiKey = "bad-key"
        }, default);

        Assert.False(result.Success);
        Assert.Equal(402, result.StatusCode);
        Assert.Contains("PAYMENT_REQUIRED", result.Message);
        Assert.Contains("Prepayment credits are depleted.", result.Message);
        Assert.DoesNotContain("bad-key", result.Message);
    }
}
