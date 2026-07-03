using System.Net.Http.Json;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class AiTaggingSettingsTests : IntegrationTestBase
{
    [Fact]
    public async Task PutThenGetAiTaggingSettings_PersistsGeminiApiKey()
    {
        using var client = Factory.CreateClient();
        var settings = new AiTaggingSettingsDto
        {
            Enabled = true,
            Model = "gemini-2.5-flash",
            ApiKey = "test-gemini-key"
        };

        using var saveResponse = await client.PutAsJsonAsync("/api/settings/ai-tagging", settings);
        saveResponse.EnsureSuccessStatusCode();

        var loaded = await client.GetFromJsonAsync<AiTaggingSettingsDto>("/api/settings/ai-tagging");

        Assert.NotNull(loaded);
        Assert.True(loaded!.Enabled);
        Assert.Equal("gemini-2.5-flash", loaded.Model);
        Assert.Equal("test-gemini-key", loaded.ApiKey);
    }
}
