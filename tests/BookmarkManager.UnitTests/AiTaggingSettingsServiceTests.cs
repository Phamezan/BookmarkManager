using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests;

public sealed class AiTaggingSettingsServiceTests
{
    [Fact]
    public async Task OldPersistedSettings_WithoutMigrationProvider_DefaultsToGemini()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ai-settings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {
              "enabled": true,
              "model": "google/gemini-2.5-flash:free",
              "groqApiKey": "legacy-groq",
              "migrationSearchModel": "groq/compound-mini",
              "migrationAutoApproveHigh": true
            }
            """);

        try
        {
            var service = new TestSettingsService(path);
            var settings = await service.GetAsync(default);

            Assert.Equal("Gemini", settings.MigrationSearchProvider);
            Assert.Equal("gemini-3.8-flash", settings.GeminiSearchModel);
            Assert.Equal(string.Empty, settings.GeminiApiKey);
            Assert.Equal("legacy-groq", settings.GroqApiKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveNormalizesProviderAndGeminiFields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ai-settings-{Guid.NewGuid():N}.json");
        try
        {
            var service = new TestSettingsService(path);
            var saved = await service.SaveAsync(new AiTaggingSettingsDto
            {
                MigrationSearchProvider = "groq",
                GeminiApiKey = "  key  ",
                GeminiSearchModel = "",
            }, default);

            Assert.Equal("Groq", saved.MigrationSearchProvider);
            Assert.Equal("key", saved.GeminiApiKey);
            Assert.Equal("gemini-3.8-flash", saved.GeminiSearchModel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class TestSettingsService(string settingsPath)
        : AiTaggingSettingsService(NullLogger<AiTaggingSettingsService>.Instance, settingsPath);
}
