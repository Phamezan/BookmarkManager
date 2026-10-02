using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests;

public sealed class AiTaggingSettingsServiceTests
{
    [Fact]
    public async Task OldPersistedSettings_WithoutMigrationProvider_DefaultsToTavily()
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

            Assert.Equal("Tavily", settings.MigrationSearchProvider);
            Assert.Equal("gemini-3.8-flash", settings.GeminiSearchModel);
            Assert.Equal(string.Empty, settings.GeminiApiKey);
            Assert.Equal("legacy-groq", settings.GroqApiKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("Gemini")]
    [InlineData("Groq")]
    [InlineData("Tavily")]
    public async Task ExplicitPersistedProvider_IsPreserved(string provider)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ai-settings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, $$"""
            {
              "migrationSearchProvider": "{{provider}}"
            }
            """);

        try
        {
            var service = new TestSettingsService(path);
            var settings = await service.GetAsync(default);

            Assert.Equal(provider, settings.MigrationSearchProvider);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EmptyPersistedProvider_FallsBackToTavily()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ai-settings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {
              "migrationSearchProvider": "   "
            }
            """);

        try
        {
            var service = new TestSettingsService(path);
            var settings = await service.GetAsync(default);

            Assert.Equal("Tavily", settings.MigrationSearchProvider);
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
                TavilyApiKey = "  tvly-key  ",
            }, default);

            Assert.Equal("Groq", saved.MigrationSearchProvider);
            Assert.Equal("key", saved.GeminiApiKey);
            Assert.Equal("gemini-3.8-flash", saved.GeminiSearchModel);
            Assert.Equal("tvly-key", saved.TavilyApiKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class TestSettingsService(string settingsPath)
        : AiTaggingSettingsService(NullLogger<AiTaggingSettingsService>.Instance, settingsPath);
}
