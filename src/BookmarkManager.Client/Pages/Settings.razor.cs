using System.Collections.Generic;
using System.Linq;
using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Settings
{
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private IBookmarkManagerApiClient ApiClient { get; set; } = default!;
    [Inject] private Microsoft.JSInterop.IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ILibraryService LibraryService { get; set; } = default!;

    private ExtensionStatusDto? _extensionStatus;
    private bool _statusLoading = true;
    private bool _linkCheckerRunning;
    private AiTaggingSettingsDto _aiSettings = new();
    private bool _aiSettingsLoading = true;
    private bool _aiSettingsSaving;

    // Curated from OpenRouter's live free-tier catalog - excludes moderation-only,
    // vision, code-only, and sub-10B models that aren't reliable for series-title extraction.
    private static readonly string[] OpenRouterFreeModels =
    {
        "nvidia/nemotron-3-ultra-550b-a55b:free",
        "nvidia/nemotron-3-super-120b-a12b:free",
        "nvidia/nemotron-nano-9b-v2:free",
        "qwen/qwen3-next-80b-a3b-instruct:free",
        "meta-llama/llama-3.3-70b-instruct:free",
        "nousresearch/hermes-3-llama-3.1-405b:free",
        "openai/gpt-oss-120b:free",
        "google/gemma-4-31b-it:free"
    };

    private IEnumerable<string> ModelOptionsIncludingCurrent =>
        string.IsNullOrWhiteSpace(_aiSettings.Model) || OpenRouterFreeModels.Contains(_aiSettings.Model)
            ? OpenRouterFreeModels
            : OpenRouterFreeModels.Append(_aiSettings.Model);
    private bool _aiKeyTesting;
    private TestAiKeyResponse? _aiKeyTestResult;
    private bool _groqKeyTesting;
    private TestAiKeyResponse? _groqKeyTestResult;

    private class ThemeOption
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string BgColor { get; set; } = string.Empty;
        public string BorderColor { get; set; } = string.Empty;
        public string TextColor { get; set; } = string.Empty;
        public string AccentColor { get; set; } = string.Empty;
    }

    private readonly List<ThemeOption> _availableThemes = new()
    {
        new ThemeOption { Id = "default", Name = "Premium Dark", Description = "Default indigo & deep obsidian style", BgColor = "#08090D", BorderColor = "rgba(255,255,255,0.06)", TextColor = "#F4F4F6", AccentColor = "#818CF8" },
        new ThemeOption { Id = "grand-line", Name = "Grand Line Gold", Description = "Weathered parchment & straw gold pirate theme", BgColor = "#0D0E12", BorderColor = "rgba(244,208,104,0.12)", TextColor = "#F4ECD8", AccentColor = "#F4D068" },
        new ThemeOption { Id = "catppuccin-mocha", Name = "Catppuccin Mocha", Description = "Cozy dark pastel cappuccino palette", BgColor = "#1e1e2e", BorderColor = "rgba(255,255,255,0.06)", TextColor = "#cdd6f4", AccentColor = "#cba6f7" },
        new ThemeOption { Id = "sakura", Name = "Sakura Sunset", Description = "Vibrant dark cherry & pink blossom theme", BgColor = "#1a1016", BorderColor = "rgba(255,255,255,0.06)", TextColor = "#fff3f8", AccentColor = "#ff79c6" }
    };

    private string _selectedThemeId = "default";

    private string _triageMatchBaseUrl = string.Empty;
    private bool _triageRunning;
    private TriageJobStatusDto? _triageResult;

    private List<ProviderHealthDto> _providerHealth = [];
    private bool _healthLoading = true;

    private LibraryCatalogSyncStatusDto? _catalogStatus;
    private bool _catalogStatusLoading = true;
    private bool _catalogResyncTriggering;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _linkCheckerRunning = await BookmarkService.IsLinkCheckRunningAsync();
        }
        catch
        {
            // Ignore failure
        }

        await Task.WhenAll(
            LoadExtensionStatusAsync(),
            LoadAiTaggingSettingsAsync(),
            LoadProviderHealthAsync(),
            LoadCatalogSyncStatusAsync());
    }

    private void OpenUrlMigrator() => Navigation.NavigateTo("/url-migrator");

    private async Task LoadProviderHealthAsync()
    {
        _healthLoading = true;
        try
        {
            _providerHealth = await LibraryService.GetProvidersHealthAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load provider health: {ex.Message}", Severity.Error);
        }
        finally
        {
            _healthLoading = false;
        }
    }

    private async Task ToggleProviderAsync(string name, bool enabled)
    {
        try
        {
            await LibraryService.ToggleProviderAsync(name, enabled);
            Snackbar.Add($"{(enabled ? "Enabled" : "Disabled")} {name} provider.", Severity.Success);
            await LoadProviderHealthAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to toggle provider: {ex.Message}", Severity.Error);
        }
    }

    private async Task RunLinkCheckerAsync()
    {
        _linkCheckerRunning = true;
        try
        {
            await BookmarkService.TriggerLinkCheckAsync();
            Snackbar.Add("Broken link checker started in the background.", Severity.Info);
            await Task.Delay(1000);
            _linkCheckerRunning = await BookmarkService.IsLinkCheckRunningAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to start link checker: {ex.Message}", Severity.Error);
        }
    }

    private async Task RunDomainTriageAsync()
    {
        if (string.IsNullOrWhiteSpace(_triageMatchBaseUrl))
        {
            Snackbar.Add("Please enter a base URL to match.", Severity.Warning);
            return;
        }

        _triageRunning = true;
        _triageResult = null;
        StateHasChanged();

        try
        {
            var request = new TriageDomainRequest(_triageMatchBaseUrl, "ManualFolder", "Broken Links");
            _triageResult = await BookmarkService.TriageDomainAsync(request);

            if (!string.IsNullOrEmpty(_triageResult.ErrorMessage))
            {
                Snackbar.Add($"Domain triage failed: {_triageResult.ErrorMessage}", Severity.Error);
            }
            else if (_triageResult.TotalFound > 0)
            {
                Snackbar.Add($"Triage complete! Moved {_triageResult.SuccessfullyProcessed} of {_triageResult.TotalFound} bookmarks.", Severity.Success);
            }
            else
            {
                Snackbar.Add("Triage complete! No matching bookmarks found.", Severity.Info);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to run domain triage: {ex.Message}", Severity.Error);
        }
        finally
        {
            _triageRunning = false;
            StateHasChanged();
        }
    }

    private async Task LoadExtensionStatusAsync()
    {
        _statusLoading = true;
        try
        {
            _extensionStatus = await ApiClient.GetAsync<ExtensionStatusDto>("/api/extension/status");
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load extension status: {ex.Message}", Severity.Error);
        }
        finally
        {
            _statusLoading = false;
        }
    }

    private async Task LoadAiTaggingSettingsAsync()
    {
        _aiSettingsLoading = true;
        try
        {
            _aiSettings = await BookmarkService.GetAiTaggingSettingsAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load AI tagging settings: {ex.Message}", Severity.Error);
        }
        finally
        {
            _aiSettingsLoading = false;
        }
    }

    private async Task TestAiKeyAsync()
    {
        _aiKeyTesting = true;
        _aiKeyTestResult = null;
        try
        {
            // Test the values currently in the form (not the saved ones) so a bad key is
            // caught before the user commits it.
            var request = new TestAiKeyRequest
            {
                BaseUrl = _aiSettings.BaseUrl,
                Model = _aiSettings.Model,
                ApiKey = _aiSettings.ApiKey
            };
            _aiKeyTestResult = await BookmarkService.TestAiTaggingKeyAsync(request);
            Snackbar.Add(
                _aiKeyTestResult.Success ? "AI key test passed." : "AI key test failed.",
                _aiKeyTestResult.Success ? Severity.Success : Severity.Error);
        }
        catch (Exception ex)
        {
            _aiKeyTestResult = new TestAiKeyResponse { Success = false, Message = ex.Message };
            Snackbar.Add($"Failed to run key test: {ex.Message}", Severity.Error);
        }
        finally
        {
            _aiKeyTesting = false;
        }
    }

    private async Task TestGroqKeyAsync()
    {
        _groqKeyTesting = true;
        _groqKeyTestResult = null;
        try
        {
            var request = new TestAiKeyRequest
            {
                Provider = "Groq",
                BaseUrl = _aiSettings.GroqBaseUrl,
                Model = _aiSettings.GroqModel,
                ApiKey = _aiSettings.GroqApiKey
            };
            _groqKeyTestResult = await BookmarkService.TestAiTaggingKeyAsync(request);
            Snackbar.Add(
                _groqKeyTestResult.Success ? "Groq key test passed." : "Groq key test failed.",
                _groqKeyTestResult.Success ? Severity.Success : Severity.Error);
        }
        catch (Exception ex)
        {
            _groqKeyTestResult = new TestAiKeyResponse { Success = false, Message = ex.Message };
            Snackbar.Add($"Failed to run Groq key test: {ex.Message}", Severity.Error);
        }
        finally
        {
            _groqKeyTesting = false;
        }
    }

    private async Task SaveAiTaggingSettingsAsync()
    {
        _aiSettingsSaving = true;
        try
        {
            _aiSettings = await BookmarkService.SaveAiTaggingSettingsAsync(_aiSettings);
            Snackbar.Add("AI tagging settings saved.", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to save AI tagging settings: {ex.Message}", Severity.Error);
        }
        finally
        {
            _aiSettingsSaving = false;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                var currentTheme = await JSRuntime.InvokeAsync<string>("localStorage.getItem", "selected-theme");
                if (!string.IsNullOrEmpty(currentTheme) && _availableThemes.Any(t => t.Id == currentTheme))
                {
                    _selectedThemeId = currentTheme;
                    StateHasChanged();
                }
            }
            catch
            {
                // Ignore failure
            }
        }
    }

    private async Task SelectThemeAsync(string themeId)
    {
        _selectedThemeId = themeId;
        try
        {
            await JSRuntime.InvokeVoidAsync("applyTheme", themeId);
            Snackbar.Add($"Theme switched to {_availableThemes.First(t => t.Id == themeId).Name}.", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to apply theme: {ex.Message}", Severity.Error);
        }
    }

    private async Task LoadCatalogSyncStatusAsync()
    {
        _catalogStatusLoading = true;
        try
        {
            _catalogStatus = await LibraryService.GetCatalogSyncStatusAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load catalog sync status: {ex.Message}", Severity.Error);
        }
        finally
        {
            _catalogStatusLoading = false;
        }
    }

    private async Task TriggerCatalogResyncAsync()
    {
        _catalogResyncTriggering = true;
        try
        {
            await LibraryService.TriggerCatalogResyncAsync();
            Snackbar.Add("Full catalog resync started in the background.", Severity.Info);
            await Task.Delay(1000);
            await LoadCatalogSyncStatusAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to trigger catalog resync: {ex.Message}", Severity.Error);
        }
        finally
        {
            _catalogResyncTriggering = false;
        }
    }
}