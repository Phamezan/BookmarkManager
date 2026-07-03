using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Settings
{
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private IBookmarkManagerApiClient ApiClient { get; set; } = default!;

    private ExtensionStatusDto? _extensionStatus;
    private bool _statusLoading = true;
    private bool _linkCheckerRunning;
    private AiTaggingSettingsDto _aiSettings = new();
    private bool _aiSettingsLoading = true;
    private bool _aiSettingsSaving;

    private string _triageMatchBaseUrl = string.Empty;
    private string _triageActionType = "ManualFolder";
    private string _triageFolderName = string.Empty;
    private bool _triageRunning;
    private TriageJobStatusDto? _triageResult;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _linkCheckerRunning = await BookmarkService.IsLinkCheckRunningAsync();

            var triageStatus = await BookmarkService.GetTriageStatusAsync();
            if (triageStatus.IsRunning)
            {
                _triageRunning = true;
                _triageResult = triageStatus;
                _ = PollTriageStatusAsync();
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
            var request = new TriageDomainRequest(_triageMatchBaseUrl, _triageActionType, _triageFolderName);
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



}