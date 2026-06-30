using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Settings
{
    [Inject] private IFolderCatalogService FolderCatalogService { get; set; } = default!;
    [Inject] private ITrackedRootService TrackedRootService { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private IBookmarkManagerApiClient ApiClient { get; set; } = default!;

    private List<FolderCandidateDto> _folderCandidates = [];
    private bool _foldersLoading = true;

    private List<TrackedRootDto> _trackedRoots = [];
    private bool _rootsLoading = true;
    
    private ExtensionStatusDto? _extensionStatus;
    private bool _statusLoading = true;
    private bool _linkCheckerRunning;

    private AiTaggingSettingsDto _aiSettings = new();
    private bool _aiSettingsLoading = true;
    private bool _aiSettingsSaving;

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
            LoadFolderCandidatesAsync(),
            LoadTrackedRootsAsync(),
            LoadExtensionStatusAsync(),
            LoadAiSettingsAsync());
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

    private async Task LoadFolderCandidatesAsync()
    {
        _foldersLoading = true;
        try
        {
            _folderCandidates = (await FolderCatalogService.GetCandidatesAsync()).ToList();
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Title, Severity.Error);
        }
        finally
        {
            _foldersLoading = false;
        }
    }

    private async Task LoadTrackedRootsAsync()
    {
        _rootsLoading = true;
        try
        {
            _trackedRoots = await TrackedRootService.GetRootsAsync();
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Title, Severity.Error);
        }
        finally
        {
            _rootsLoading = false;
        }
    }

    private async Task PromptRemoveRootAsync(Guid id, string title)
    {
        var parameters = new DialogParameters
        {
            ["Message"] = $"Stop tracking \"{title}\"? Existing bookmarks remain; new Brave changes to this folder will be ignored.",
            ["ConfirmText"] = "Stop tracking",
            ["CancelText"] = "Cancel"
        };
        var dialog = await DialogService.ShowAsync<ConfirmDialog>("Stop tracking root", parameters);
        var result = await dialog.Result;
        if (result?.Canceled != false) return;

        try
        {
            await TrackedRootService.RemoveRootAsync(id);
            await LoadTrackedRootsAsync();
            Snackbar.Add("Tracked root removed", Severity.Success);
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Detail ?? ex.Title, Severity.Error);
        }
    }

    private async Task AddTrackedRootAsync(FolderCandidateDto candidate)
    {
        try
        {
            await TrackedRootService.AddRootAsync(candidate.Title, null, candidate.BrowserNodeId);
            await Task.WhenAll(LoadTrackedRootsAsync(), LoadFolderCandidatesAsync());
            Snackbar.Add($"Tracking {candidate.Title}", Severity.Success);
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Detail ?? ex.Title, Severity.Error);
        }
    }

    private async Task SyncRootAsync(Guid id)
    {
        if (await TrackedRootService.SyncRootAsync(id))
        {
            await LoadTrackedRootsAsync();
            Snackbar.Add("Root sync requested", Severity.Success);
        }
    }

    private async Task PromptRepairRootAsync(Guid id, string title)
    {
        var parameters = new DialogParameters
        {
            ["Message"] = $"Request a Brave-wins repair snapshot for \"{title}\"? This will reset its sync status and force a full overwrite from Brave on the next heartbeat check-in.",
            ["ConfirmText"] = "Request repair",
            ["CancelText"] = "Cancel"
        };
        var dialog = await DialogService.ShowAsync<ConfirmDialog>("Repair snapshot", parameters);
        var result = await dialog.Result;
        if (result?.Canceled != false) return;

        try
        {
            if (await TrackedRootService.RepairRootAsync(id))
            {
                await LoadTrackedRootsAsync();
                Snackbar.Add($"Repair snapshot queued for \"{title}\"", Severity.Warning);
            }
        }
        catch (ApiException ex)
        {
            Snackbar.Add(ex.Detail ?? ex.Title, Severity.Error);
        }
    }

    private string _selectedProvider = "OpenAI";
    private string _selectedModel = "gpt-4o-mini";
    private bool _customModelActive;

    private async Task LoadAiSettingsAsync()
    {
        _aiSettingsLoading = true;
        try
        {
            _aiSettings = await BookmarkService.GetAiTaggingSettingsAsync() ?? new();
            DetectProviderAndModel();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load AI settings: {ex.Message}", Severity.Error);
        }
        finally
        {
            _aiSettingsLoading = false;
        }
    }

    private void DetectProviderAndModel()
    {
        if (string.IsNullOrEmpty(_aiSettings.Endpoint))
        {
            _selectedProvider = "OpenAI";
            _aiSettings.Endpoint = "https://api.openai.com/v1/chat/completions";
        }
        else if (_aiSettings.Endpoint.Contains("openai.com", StringComparison.OrdinalIgnoreCase))
        {
            _selectedProvider = "OpenAI";
        }
        else if (_aiSettings.Endpoint.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
        {
            _selectedProvider = "OpenRouter";
        }
        else if (_aiSettings.Endpoint.Contains("localhost:11434", StringComparison.OrdinalIgnoreCase) || _aiSettings.Endpoint.Contains("127.0.0.1:11434", StringComparison.OrdinalIgnoreCase))
        {
            _selectedProvider = "Ollama";
        }
        else
        {
            _selectedProvider = "Custom";
        }

        var commonModels = GetModelsForProvider(_selectedProvider);
        if (commonModels.Contains(_aiSettings.Model, StringComparer.OrdinalIgnoreCase))
        {
            _selectedModel = _aiSettings.Model;
            _customModelActive = false;
        }
        else if (string.IsNullOrWhiteSpace(_aiSettings.Model))
        {
            _selectedModel = commonModels.FirstOrDefault() ?? string.Empty;
            _aiSettings.Model = _selectedModel;
            _customModelActive = false;
        }
        else
        {
            _selectedModel = "Custom";
            _customModelActive = true;
        }
    }

    private List<string> GetModelsForProvider(string provider)
    {
        return provider switch
        {
            "OpenAI" => ["gpt-4o-mini", "gpt-4o", "gpt-3.5-turbo"],
            "OpenRouter" => [
                "openrouter/free",
                "google/gemini-2.5-flash:free",
                "meta-llama/llama-3-8b-instruct:free",
                "qwen/qwen-2.5-7b-instruct:free",
                "microsoft/phi-3-medium-128k-instruct:free",
                "mistralai/mistral-7b-instruct:free"
            ],
            "Ollama" => ["llama3", "mistral", "phi3"],
            _ => []
        };
    }

    private void OnProviderChanged(string provider)
    {
        _selectedProvider = provider;
        _aiSettings.Endpoint = provider switch
        {
            "OpenAI" => "https://api.openai.com/v1/chat/completions",
            "OpenRouter" => "https://openrouter.ai/api/v1/chat/completions",
            "Ollama" => "http://localhost:11434/v1/chat/completions",
            _ => _aiSettings.Endpoint
        };

        var models = GetModelsForProvider(provider);
        if (models.Count > 0)
        {
            _selectedModel = models[0];
            _aiSettings.Model = _selectedModel;
            _customModelActive = false;
        }
        else
        {
            _selectedModel = "Custom";
            _customModelActive = true;
        }
    }

    private void OnModelChanged(string model)
    {
        _selectedModel = model;
        if (model == "Custom")
        {
            _customModelActive = true;
            _aiSettings.Model = string.Empty;
        }
        else
        {
            _customModelActive = false;
            _aiSettings.Model = model;
        }
    }

    private async Task SaveAiSettingsAsync()
    {
        _aiSettingsSaving = true;
        try
        {
            var success = await BookmarkService.SaveAiTaggingSettingsAsync(_aiSettings);
            if (success)
            {
                Snackbar.Add("AI Tagging settings saved successfully.", Severity.Success);
            }
            else
            {
                Snackbar.Add("Failed to save AI Tagging settings.", Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error saving AI settings: {ex.Message}", Severity.Error);
        }
        finally
        {
            _aiSettingsSaving = false;
        }
    }
}