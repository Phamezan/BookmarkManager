using System.Net;
using System.Net.Http.Json;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class AiTaggingSettingsTests : IntegrationTestBase
{
    private const string OpenRouterKey = "sk-openrouter-REAL-a1b2";
    private const string GroqKey = "gsk-groq-REAL-c3d4";
    private const string GeminiKey = "AIza-gemini-REAL-e5f6";
    private const string TavilyKey = "tvly-tavily-REAL-k1l2";
    private const string RagKey = "gsk-rag-REAL-g7h8";
    private const string RagFallbackKey = "nvapi-ragfallback-REAL-i9j0";

    private static AiTaggingSettingsDto SeedSettings() => new()
    {
        Enabled = true,
        ApiKey = OpenRouterKey,
        GroqApiKey = GroqKey,
        GeminiApiKey = GeminiKey,
        TavilyApiKey = TavilyKey,
        RagApiKey = RagKey,
        RagFallbackApiKey = RagFallbackKey
    };

    private async Task SeedAsync()
    {
        var service = Factory.Services.GetRequiredService<AiTaggingSettingsService>();
        await service.SaveAsync(SeedSettings(), default);
    }

    private async Task<AiTaggingSettingsDto> ReadStoredAsync()
    {
        var service = Factory.Services.GetRequiredService<AiTaggingSettingsService>();
        return await service.GetAsync(default);
    }

    [Fact]
    public async Task GetAiTaggingSettings_MasksEverySecretAndReportsHasFlags()
    {
        await SeedAsync();
        using var client = Factory.CreateClient();

        var body = await client.GetStringAsync("/api/settings/ai-tagging");

        Assert.DoesNotContain(OpenRouterKey, body);
        Assert.DoesNotContain(GroqKey, body);
        Assert.DoesNotContain(GeminiKey, body);
        Assert.DoesNotContain(TavilyKey, body);
        Assert.DoesNotContain(RagKey, body);
        Assert.DoesNotContain(RagFallbackKey, body);

        var loaded = await client.GetFromJsonAsync<AiTaggingSettingsDto>("/api/settings/ai-tagging");
        Assert.NotNull(loaded);
        Assert.True(loaded!.HasApiKey);
        Assert.True(loaded.HasGroqApiKey);
        Assert.True(loaded.HasGeminiApiKey);
        Assert.True(loaded.HasTavilyApiKey);
        Assert.True(loaded.HasRagApiKey);
        Assert.True(loaded.HasRagFallbackApiKey);
        Assert.Equal("••••" + OpenRouterKey[^4..], loaded.ApiKey);
        Assert.Equal("••••" + GroqKey[^4..], loaded.GroqApiKey);
        Assert.Equal("••••" + GeminiKey[^4..], loaded.GeminiApiKey);
        Assert.Equal("••••" + TavilyKey[^4..], loaded.TavilyApiKey);
        Assert.Equal("••••" + RagKey[^4..], loaded.RagApiKey);
        Assert.Equal("••••" + RagFallbackKey[^4..], loaded.RagFallbackApiKey);
    }

    [Fact]
    public async Task GetAiTaggingSettings_WhenNoKeysSet_ReturnsEmptyAndHasFlagsFalse()
    {
        using var client = Factory.CreateClient();

        var loaded = await client.GetFromJsonAsync<AiTaggingSettingsDto>("/api/settings/ai-tagging");

        Assert.NotNull(loaded);
        Assert.False(loaded!.HasApiKey);
        Assert.False(loaded.HasGroqApiKey);
        Assert.False(loaded.HasGeminiApiKey);
        Assert.False(loaded.HasTavilyApiKey);
        Assert.False(loaded.HasRagApiKey);
        Assert.False(loaded.HasRagFallbackApiKey);
        Assert.Equal(string.Empty, loaded.ApiKey);
        Assert.Equal(string.Empty, loaded.TavilyApiKey);
    }

    [Fact]
    public async Task PutAiTaggingSettings_WithMaskedOrEmptyKeys_KeepsStoredKeys()
    {
        await SeedAsync();
        using var client = Factory.CreateClient();

        // Round-trip exactly what GET returned (masked secrets) - must not overwrite.
        var loaded = await client.GetFromJsonAsync<AiTaggingSettingsDto>("/api/settings/ai-tagging");
        loaded!.Model = "some-new-model";
        using var maskedResponse = await client.PutAsJsonAsync("/api/settings/ai-tagging", loaded);
        maskedResponse.EnsureSuccessStatusCode();

        var afterMasked = await ReadStoredAsync();
        Assert.Equal(OpenRouterKey, afterMasked.ApiKey);
        Assert.Equal(GroqKey, afterMasked.GroqApiKey);
        Assert.Equal(GeminiKey, afterMasked.GeminiApiKey);
        Assert.Equal(TavilyKey, afterMasked.TavilyApiKey);
        Assert.Equal(RagKey, afterMasked.RagApiKey);
        Assert.Equal(RagFallbackKey, afterMasked.RagFallbackApiKey);
        Assert.Equal("some-new-model", afterMasked.Model);

        // Untouched inputs come back empty - still must not overwrite.
        using var emptyResponse = await client.PutAsJsonAsync("/api/settings/ai-tagging", new AiTaggingSettingsDto
        {
            ApiKey = string.Empty,
            GroqApiKey = "   ",
            GeminiApiKey = null!,
            TavilyApiKey = string.Empty,
            RagApiKey = string.Empty,
            RagFallbackApiKey = string.Empty
        });
        emptyResponse.EnsureSuccessStatusCode();

        var afterEmpty = await ReadStoredAsync();
        Assert.Equal(OpenRouterKey, afterEmpty.ApiKey);
        Assert.Equal(GroqKey, afterEmpty.GroqApiKey);
        Assert.Equal(GeminiKey, afterEmpty.GeminiApiKey);
        Assert.Equal(TavilyKey, afterEmpty.TavilyApiKey);
        Assert.Equal(RagKey, afterEmpty.RagApiKey);
        Assert.Equal(RagFallbackKey, afterEmpty.RagFallbackApiKey);
    }

    [Fact]
    public async Task PutAiTaggingSettings_WithNewKey_ReplacesOnlyThatKey_AndResponseIsMasked()
    {
        await SeedAsync();
        using var client = Factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/settings/ai-tagging", new AiTaggingSettingsDto
        {
            ApiKey = "sk-openrouter-NEW-x9y8"
        });
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sk-openrouter-NEW-x9y8", responseBody);
        Assert.DoesNotContain(OpenRouterKey, responseBody);
        Assert.DoesNotContain(GroqKey, responseBody);

        var stored = await ReadStoredAsync();
        Assert.Equal("sk-openrouter-NEW-x9y8", stored.ApiKey);
        Assert.Equal(GroqKey, stored.GroqApiKey);
        Assert.Equal(GeminiKey, stored.GeminiApiKey);

        var returned = System.Text.Json.JsonSerializer.Deserialize<AiTaggingSettingsDto>(
            responseBody, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.True(returned!.HasApiKey);
        Assert.Equal("••••x9y8", returned.ApiKey);
    }

    [Fact]
    public async Task PutAiTaggingSettings_WithClearFlag_ClearsKey()
    {
        await SeedAsync();
        using var client = Factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/settings/ai-tagging", new AiTaggingSettingsDto
        {
            ClearGroqApiKey = true
        });
        response.EnsureSuccessStatusCode();

        var stored = await ReadStoredAsync();
        Assert.Equal(string.Empty, stored.GroqApiKey);
        Assert.Equal(OpenRouterKey, stored.ApiKey);

        var returned = await response.Content.ReadFromJsonAsync<AiTaggingSettingsDto>();
        Assert.False(returned!.HasGroqApiKey);
        Assert.Equal(string.Empty, returned.GroqApiKey);
        Assert.True(returned.HasApiKey);
    }

    [Fact]
    public async Task PutAiTaggingSettings_WithTavilyClearFlag_ClearsOnlyTavilyKey()
    {
        await SeedAsync();
        using var client = Factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/settings/ai-tagging", new AiTaggingSettingsDto
        {
            ClearTavilyApiKey = true
        });
        response.EnsureSuccessStatusCode();

        var stored = await ReadStoredAsync();
        Assert.Equal(string.Empty, stored.TavilyApiKey);
        Assert.Equal(GeminiKey, stored.GeminiApiKey);
        Assert.Equal(GroqKey, stored.GroqApiKey);

        var returned = await response.Content.ReadFromJsonAsync<AiTaggingSettingsDto>();
        Assert.False(returned!.HasTavilyApiKey);
        Assert.Equal(string.Empty, returned.TavilyApiKey);
        Assert.True(returned.HasGeminiApiKey);
    }

    [Fact]
    public async Task PutAiTaggingSettings_WithForeignTavilyMask_Returns400AndSavesNothing()
    {
        await SeedAsync();
        using var client = Factory.CreateClient();

        // Paste the Gemini key's mask into the Tavily field: it must not be stored as a literal key.
        using var response = await client.PutAsJsonAsync("/api/settings/ai-tagging", new AiTaggingSettingsDto
        {
            TavilyApiKey = "••••" + GeminiKey[^4..]
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Tavily", body);

        var stored = await ReadStoredAsync();
        Assert.Equal(TavilyKey, stored.TavilyApiKey);
        Assert.DoesNotContain("••••", stored.TavilyApiKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PutAiTaggingSettings_WithPreservedKeyAndChangedUrl_Returns400AndSavesNothing(bool useMasked)
    {
        const string attackerBaseUrl = "https://attacker.example/v1";
        await SeedAsync();
        using var client = Factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/settings/ai-tagging", new AiTaggingSettingsDto
        {
            GroqBaseUrl = attackerBaseUrl,
            GroqApiKey = useMasked ? "••••" + GroqKey[^4..] : string.Empty
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Re-enter the Groq API key when changing its endpoint", body);

        var stored = await ReadStoredAsync();
        Assert.Equal(GroqKey, stored.GroqApiKey);
        Assert.Equal("https://api.groq.com/openai/v1", stored.GroqBaseUrl);
        Assert.DoesNotContain("attacker.example", stored.GroqBaseUrl);
    }

    [Fact]
    public async Task PutAiTaggingSettings_WithNewKeyAndChangedUrl_Saves()
    {
        const string newBaseUrl = "https://new.groq.example/v1";
        await SeedAsync();
        using var client = Factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/settings/ai-tagging", new AiTaggingSettingsDto
        {
            GroqBaseUrl = newBaseUrl,
            GroqApiKey = "gsk-brand-new"
        });
        response.EnsureSuccessStatusCode();

        var stored = await ReadStoredAsync();
        Assert.Equal(newBaseUrl, stored.GroqBaseUrl);
        Assert.Equal("gsk-brand-new", stored.GroqApiKey);
    }

    [Fact]
    public async Task PutAiTaggingSettings_WithChangedUrlAndNoStoredKey_Saves()
    {
        const string newBaseUrl = "https://first.groq.example/v1";
        using var client = Factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/settings/ai-tagging", new AiTaggingSettingsDto
        {
            GroqBaseUrl = newBaseUrl,
            GroqApiKey = string.Empty
        });
        response.EnsureSuccessStatusCode();

        var stored = await ReadStoredAsync();
        Assert.Equal(newBaseUrl, stored.GroqBaseUrl);
        Assert.Equal(string.Empty, stored.GroqApiKey);
    }

    [Fact]
    public async Task PutAiTaggingSettings_WithForeignMaskValue_Returns400AndSavesNothing()
    {
        await SeedAsync();
        using var client = Factory.CreateClient();

        // Paste the OpenRouter key's mask into the Groq field: it must not be stored as a literal key.
        using var response = await client.PutAsJsonAsync("/api/settings/ai-tagging", new AiTaggingSettingsDto
        {
            GroqApiKey = "••••" + OpenRouterKey[^4..]
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Groq", body);

        var stored = await ReadStoredAsync();
        Assert.Equal(GroqKey, stored.GroqApiKey);
        Assert.DoesNotContain("••••", stored.GroqApiKey);
    }

    [Fact]
    public async Task PutAiTaggingSettings_WithPreservedApiKeyAndChangedEndpoint_Returns400AndSavesNothing()
    {
        var service = Factory.Services.GetRequiredService<AiTaggingSettingsService>();
        await service.SaveAsync(new AiTaggingSettingsDto { ApiKey = OpenRouterKey }, default);
        using var client = Factory.CreateClient();

        using var response = await client.PutAsJsonAsync("/api/settings/ai-tagging", new AiTaggingSettingsDto
        {
            ApiKey = string.Empty,
            Endpoint = "https://attacker.example/v1beta"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("when changing its endpoint", body);

        var stored = await ReadStoredAsync();
        Assert.Equal(OpenRouterKey, stored.ApiKey);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta", stored.Endpoint);
        Assert.DoesNotContain("attacker.example", stored.Endpoint);
    }

    [Fact]
    public async Task TestConnection_WithEmptyOrMaskedKey_UsesStoredKey()
    {
        await SeedAsync();
        using var factory = new FakeAiClientFactory();
        var fake = factory.AiClient;
        var service = factory.Services.GetRequiredService<AiTaggingSettingsService>();
        await service.SaveAsync(SeedSettings(), default);

        using var client = factory.CreateClient();

        using var empty = await client.PostAsJsonAsync("/api/settings/ai-tagging/test", new TestAiKeyRequest
        {
            Provider = "Groq",
            SecretName = "GroqApiKey",
            Model = "llama-3.3-70b-versatile",
            ApiKey = string.Empty
        });
        empty.EnsureSuccessStatusCode();
        Assert.Equal(GroqKey, fake.LastRequest!.ApiKey);

        using var masked = await client.PostAsJsonAsync("/api/settings/ai-tagging/test", new TestAiKeyRequest
        {
            Provider = "Groq",
            SecretName = "GroqApiKey",
            Model = "llama-3.3-70b-versatile",
            ApiKey = "••••" + GroqKey[^4..]
        });
        masked.EnsureSuccessStatusCode();
        Assert.Equal(GroqKey, fake.LastRequest!.ApiKey);

        using var typed = await client.PostAsJsonAsync("/api/settings/ai-tagging/test", new TestAiKeyRequest
        {
            Provider = "Groq",
            SecretName = "GroqApiKey",
            Model = "llama-3.3-70b-versatile",
            ApiKey = "gsk-typed-key"
        });
        typed.EnsureSuccessStatusCode();
        Assert.Equal("gsk-typed-key", fake.LastRequest!.ApiKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestConnection_WhenStoredKeyUsed_PinsToStoredBaseUrl_NotCallerSupplied(bool useMasked)
    {
        const string storedGroqBaseUrl = "https://stored.groq.example/v1";
        const string attackerBaseUrl = "https://attacker.example/v1";

        using var factory = new FakeAiClientFactory();
        var fake = factory.AiClient;
        var service = factory.Services.GetRequiredService<AiTaggingSettingsService>();
        var seed = SeedSettings();
        seed.GroqBaseUrl = storedGroqBaseUrl;
        await service.SaveAsync(seed, default);

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/settings/ai-tagging/test", new TestAiKeyRequest
        {
            Provider = "Groq",
            SecretName = "GroqApiKey",
            Model = "llama-3.3-70b-versatile",
            BaseUrl = attackerBaseUrl,
            ApiKey = useMasked ? "••••" + GroqKey[^4..] : string.Empty
        });
        response.EnsureSuccessStatusCode();

        Assert.Equal(GroqKey, fake.LastRequest!.ApiKey);
        Assert.Equal(storedGroqBaseUrl, fake.LastRequest.BaseUrl);
        Assert.DoesNotContain("attacker.example", fake.LastRequest.BaseUrl);
    }

    [Fact]
    public async Task TestConnection_WithTypedKey_KeepsCallerSuppliedBaseUrl()
    {
        using var factory = new FakeAiClientFactory();
        var fake = factory.AiClient;
        var service = factory.Services.GetRequiredService<AiTaggingSettingsService>();
        await service.SaveAsync(SeedSettings(), default);

        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/settings/ai-tagging/test", new TestAiKeyRequest
        {
            Provider = "Groq",
            SecretName = "GroqApiKey",
            Model = "llama-3.3-70b-versatile",
            BaseUrl = "https://typed.example/v1",
            ApiKey = "gsk-typed-key"
        });
        response.EnsureSuccessStatusCode();

        Assert.Equal("gsk-typed-key", fake.LastRequest!.ApiKey);
        Assert.Equal("https://typed.example/v1", fake.LastRequest.BaseUrl);
    }

    private sealed class FakeAiClientFactory : IntegrationTestWebApplicationFactory
    {
        public FakeAiSeriesClient AiClient { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAiSeriesIdentificationClient>();
                services.AddSingleton<IAiSeriesIdentificationClient>(AiClient);
            });
        }
    }

    private sealed class FakeAiSeriesClient : IAiSeriesIdentificationClient
    {
        public TestAiKeyRequest? LastRequest { get; private set; }

        public Task<AiProviderResponse> IdentifyAsync(AiSeriesIdentifyRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<TestAiKeyResponse> TestConnectionAsync(TestAiKeyRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new TestAiKeyResponse { Success = true, StatusCode = 200, Message = "fake-ok" });
        }
    }
}
