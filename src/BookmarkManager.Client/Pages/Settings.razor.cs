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

    private List<FolderCandidateDto> _folderCandidates = [];
    private bool _foldersLoading = true;

    private List<TrackedRootDto> _trackedRoots = [];
    private bool _rootsLoading = true;
    private string _themePreference = "System";
    private string _densityPreference = "Comfortable";
    private bool _showSyncHints = true;

    protected override async Task OnInitializedAsync()
    {
        await Task.WhenAll(
            LoadFolderCandidatesAsync(),
            LoadTrackedRootsAsync());
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

    private async Task PromptRepairAsync()
    {
        var parameters = new DialogParameters
        {
            ["Message"] = "Request a Brave-wins repair snapshot for all active tracked roots? This can overwrite stale server projection data.",
            ["ConfirmText"] = "Request repair",
            ["CancelText"] = "Cancel"
        };
        var dialog = await DialogService.ShowAsync<ConfirmDialog>("Repair snapshot", parameters);
        var result = await dialog.Result;
        if (result?.Canceled != false) return;

        Snackbar.Add("Repair snapshot request queued", Severity.Warning);
    }

    private static string DefaultCategory(string title)
        => title.Contains("Manga", StringComparison.OrdinalIgnoreCase) ? "Manga"
            : title.Contains("Novel", StringComparison.OrdinalIgnoreCase) ? "Novels"
            : title.Contains("Anime", StringComparison.OrdinalIgnoreCase) ? "Anime"
            : "Other";
}