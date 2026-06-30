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
            LoadExtensionStatusAsync());
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

}