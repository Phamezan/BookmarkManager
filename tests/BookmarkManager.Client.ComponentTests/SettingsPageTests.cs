using BookmarkManager.Client.ComponentTests.TestDoubles;
using BookmarkManager.Client.Pages;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class SettingsPageTests
{
    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage(BunitContext context)
    {
        return context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudBlazor.MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.CloseComponent();
        });
    }

    [Fact]
    public async Task UrlMigratorTab_RendersAndBindsProviderKeyAndModel()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        AiTaggingSettingsDto? saved = null;
        var fake = new FakeBookmarkService
        {
            OnSaveAiTaggingSettings = settings =>
            {
                saved = settings;
                return Task.FromResult(settings);
            }
        };
        context.Services.AddSingleton<IBookmarkService>(fake);
        context.Services.AddSingleton<IBookmarkManagerApiClient>(new FakeApiClient());
        context.Services.AddSingleton<ILibraryService>(new StubLibraryService());

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".mud-tab")));
        page.FindAll(".mud-tab").First(t => t.TextContent.Trim() == "URL Migrator").Click();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".migration-search-provider-field")));
        Assert.NotEmpty(page.FindAll(".gemini-search-model-field"));
        Assert.NotEmpty(page.FindAll(".gemini-api-key-field"));
        Assert.NotEmpty(page.FindAll(".tavily-api-key-field"));
        Assert.Contains("Tavily (search API)", page.Markup);

        var keyInput = page.Find(".gemini-api-key-field input");
        keyInput.Input("new-gemini-key");
        var tavilyInput = page.Find(".tavily-api-key-field input");
        tavilyInput.Input("new-tavily-key");

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button")));
        page.FindAll("button").First(b => b.TextContent.Contains("Save all")).Click();

        page.WaitForAssertion(() => Assert.NotNull(saved));
        Assert.Equal("new-gemini-key", saved!.GeminiApiKey);
        Assert.Equal("new-tavily-key", saved.TavilyApiKey);
        Assert.Equal("Tavily", saved.MigrationSearchProvider);
    }

    [Fact]
    public async Task AiTaggingKey_ShowsSavedHint_AndSaveDoesNotSendMaskedValueAsReplacement()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        AiTaggingSettingsDto? saved = null;
        var fake = new FakeBookmarkService
        {
            AiTaggingSettings = new AiTaggingSettingsDto
            {
                Enabled = true,
                HasApiKey = true,
                ApiKey = "••••a1b2"
            },
            OnSaveAiTaggingSettings = settings =>
            {
                saved = settings;
                return Task.FromResult(settings);
            }
        };
        context.Services.AddSingleton<IBookmarkService>(fake);
        context.Services.AddSingleton<IBookmarkManagerApiClient>(new FakeApiClient());
        context.Services.AddSingleton<ILibraryService>(new StubLibraryService());

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.Contains("Saved (••••a1b2)", page.Markup));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button")));
        page.FindAll("button").First(b => b.TextContent.Contains("Save all")).Click();

        page.WaitForAssertion(() => Assert.NotNull(saved));
        Assert.Equal(string.Empty, saved!.ApiKey);
        Assert.False(saved.ClearApiKey);
    }

    private sealed class FakeApiClient : IBookmarkManagerApiClient
    {
        public Task<T?> GetAsync<T>(string uri, CancellationToken cancellationToken = default) => Task.FromResult<T?>(default);
        public Task<T?> SendAsync<T>(HttpMethod method, string uri, object? body = null, CancellationToken cancellationToken = default) => Task.FromResult<T?>(default);
        public Task SendAsync(HttpMethod method, string uri, object? body = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubLibraryService : ILibraryService
    {
        public Task<LibrarySearchResponse> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken = default) => Task.FromResult(new LibrarySearchResponse());
        public Task<LibrarySearchResponse> GetTrendingAsync(LibraryMediaType? mediaType, int skip = 0, int take = 48, CancellationToken cancellationToken = default) => Task.FromResult(new LibrarySearchResponse());
        public Task<LibraryCatalogSyncStatusDto> GetCatalogSyncStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LibraryCatalogSyncStatusDto());
        public Task TriggerCatalogResyncAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<LibraryEntryDto?> EnrichCatalogEntryAsync(string provider, string providerId, CancellationToken cancellationToken = default) => Task.FromResult<LibraryEntryDto?>(null);
        public Task<List<ProviderHealthDto>> GetProvidersHealthAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<ProviderHealthDto>());
        public Task ToggleProviderAsync(string providerName, bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<List<LibraryReadingProgressDto>> GetReadingProgressAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<LibraryReadingProgressDto>());
        public Task<List<LibraryEntryDto>> GetMyBookmarkedSeriesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<LibraryEntryDto>());
        public Task<List<LibraryEntryDto>> GetSavedForLaterAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<LibraryEntryDto>());
    }
}
