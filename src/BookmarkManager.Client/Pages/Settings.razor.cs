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

    private ExtensionStatusDto? _extensionStatus;
    private bool _statusLoading = true;
    private bool _linkCheckerRunning;
    private AiTaggingSettingsDto _aiSettings = new();
    private bool _aiSettingsLoading = true;
    private bool _aiSettingsSaving;
    private bool _aiKeyTesting;
    private TestAiKeyResponse? _aiKeyTestResult;

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
        new ThemeOption { Id = "catppuccin-mocha", Name = "Catppuccin Mocha", Description = "Cozy dark pastel cappuccino palette", BgColor = "#1e1e2e", BorderColor = "rgba(255,255,255,0.06)", TextColor = "#cdd6f4", AccentColor = "#cba6f7" },
        new ThemeOption { Id = "catppuccin-latte", Name = "Catppuccin Latte", Description = "Warm cream light cappuccino style", BgColor = "#eff1f5", BorderColor = "rgba(0,0,0,0.08)", TextColor = "#4c4f69", AccentColor = "#8839ef" },
        new ThemeOption { Id = "sakura", Name = "Sakura Sunset", Description = "Vibrant dark cherry & pink blossom theme", BgColor = "#1a1016", BorderColor = "rgba(255,255,255,0.06)", TextColor = "#fff3f8", AccentColor = "#ff79c6" },
        new ThemeOption { Id = "cyberpunk", Name = "Cyberpunk Neon", Description = "Futuristic neon pink and cyan layout", BgColor = "#030008", BorderColor = "rgba(0,255,255,0.15)", TextColor = "#00ffff", AccentColor = "#ff007f" }
    };

    private string _selectedThemeId = "default";

    private string _triageMatchBaseUrl = string.Empty;
    private string _triageActionType = "ManualFolder";
    private bool _triageRunning;
    private TriageJobStatusDto? _triageResult;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _linkCheckerRunning = await BookmarkService.IsLinkCheckRunningAsync();

            var triageStatus = await BookmarkService.GetTriageStatusAsync();
            if (triageStatus.IsRunning || triageStatus.TotalFound > 0)
            {
                _triageResult = triageStatus;
                _triageRunning = triageStatus.IsRunning;
                if (_triageRunning)
                {
                    _ = PollTriageStatusAsync();
                }
            }
        }
        catch
        {
            // Ignore failure
        }

        await Task.WhenAll(LoadExtensionStatusAsync(), LoadAiTaggingSettingsAsync());
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
            var request = new TriageDomainRequest(_triageMatchBaseUrl, _triageActionType, "Broken Links");
            _triageResult = await BookmarkService.TriageDomainAsync(request);
            
            _ = PollTriageStatusAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to start domain triage: {ex.Message}", Severity.Error);
            _triageRunning = false;
            StateHasChanged();
        }
    }

    private async Task PollTriageStatusAsync()
    {
        try
        {
            while (_triageRunning)
            {
                await Task.Delay(1000);
                var status = await BookmarkService.GetTriageStatusAsync();
                
                _triageResult = status;
                _triageRunning = status.IsRunning;
                
                StateHasChanged();
            }

            if (_triageResult != null && !string.IsNullOrEmpty(_triageResult.ErrorMessage))
            {
                Snackbar.Add($"Domain triage failed: {_triageResult.ErrorMessage}", Severity.Error);
            }
            else if (_triageResult != null && _triageResult.TotalFound > 0)
            {
                Snackbar.Add($"Triage complete! Processed {_triageResult.SuccessfullyProcessed} of {_triageResult.TotalFound} bookmarks.", Severity.Success);
            }
            else if (_triageResult != null)
            {
                Snackbar.Add("Triage complete! No matching bookmarks found.", Severity.Info);
            }
        }
        catch
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
}