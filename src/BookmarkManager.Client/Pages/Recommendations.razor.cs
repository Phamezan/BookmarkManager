using System.Text.Json;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Recommendations : IDisposable
{
    private const string StorageKey = "recommendations.folderIds";
    private const int RecommendationCount = 15;
    private static readonly TimeSpan SpotlightInterval = TimeSpan.FromSeconds(6);

    private bool _loading = true;
    private bool _spinning;
    private bool _spotlightPaused;
    private Guid? _spotlightId;
    private CancellationTokenSource? _spotlightCts;
    private List<BookmarkNodeDto> _recommendations = [];
    private List<(Guid Id, string Title, int Depth)> _flatFolders = [];
    private HashSet<Guid> _selectedFolderIds = [];
    private DotNetObjectReference<Recommendations>? _dotNetRef;
    private bool _swipeInitialized;

    private BookmarkNodeDto? SpotlightItem =>
        _spotlightId is { } id ? _recommendations.FirstOrDefault(r => r.Id == id) : null;

    private List<BookmarkNodeDto> GridItems =>
        _spotlightId is null
            ? _recommendations
            : _recommendations.Where(r => r.Id != _spotlightId).ToList();

    protected override async Task OnInitializedAsync()
    {
        var tree = await BookmarkService.GetFolderTreeAsync();
        _flatFolders = FlattenFolders(tree, 0);

        _selectedFolderIds = await LoadPersistedFolderIdsAsync();

        if (_selectedFolderIds.Count > 0)
        {
            await LoadRecommendationsAsync();
        }
        else
        {
            _loading = false;
        }
    }

    private static List<(Guid Id, string Title, int Depth)> FlattenFolders(List<FolderTreeNodeDto> nodes, int depth)
    {
        var result = new List<(Guid, string, int)>();
        foreach (var node in nodes)
        {
            result.Add((node.Id, node.Title, depth));
            result.AddRange(FlattenFolders(node.Children, depth + 1));
        }
        return result;
    }

    private async Task<HashSet<Guid>> LoadPersistedFolderIdsAsync()
    {
        try
        {
            var json = await JSRuntime.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrEmpty(json)) return [];
            return new HashSet<Guid>(JsonSerializer.Deserialize<List<Guid>>(json) ?? []);
        }
        catch
        {
            return [];
        }
    }

    private async Task PersistFolderIdsAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_selectedFolderIds);
            await JSRuntime.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
        }
        catch
        {
            // best-effort persistence only
        }
    }

    private async Task ToggleFolderAsync(Guid id, bool isSelected)
    {
        if (isSelected) _selectedFolderIds.Add(id);
        else _selectedFolderIds.Remove(id);

        await PersistFolderIdsAsync();
        await LoadRecommendationsAsync();
    }

    private async Task ClearFoldersAsync()
    {
        if (_selectedFolderIds.Count == 0) return;
        _selectedFolderIds.Clear();
        await PersistFolderIdsAsync();
        await LoadRecommendationsAsync();
    }

    private async Task ReshuffleAsync()
    {
        _spinning = true;
        StateHasChanged();
        await LoadRecommendationsAsync();
        _spinning = false;
    }

    private async Task LoadRecommendationsAsync()
    {
        _loading = true;
        StateHasChanged();
        try
        {
            _recommendations = await BookmarkService.GetRecommendationsAsync(_selectedFolderIds.ToList(), RecommendationCount);
            _spotlightId = _recommendations.Count > 0 ? _recommendations[0].Id : null;
            StartSpotlightRotation();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load recommendations: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private static string GetHost(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return url;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _dotNetRef = DotNetObjectReference.Create(this);
        }

        // .rec-more-list only exists once folders are selected and results load,
        // so keep retrying (cheaply — the JS side no-ops once bound) until it does.
        if (!_swipeInitialized)
        {
            try
            {
                await JSRuntime.InvokeVoidAsync("initRecommendationSwipe", ".rec-more-list", _dotNetRef);
                _swipeInitialized = true;
            }
            catch
            {
                // Safe fallback during unmounting or before the list exists yet
            }
        }
    }

    [JSInvokable]
    public async Task OnRowSwipeDismissed(string idString)
    {
        if (!Guid.TryParse(idString, out var id)) return;
        var item = _recommendations.FirstOrDefault(r => r.Id == id);
        if (item is null) return;
        await ArchiveAsync(item);
        StateHasChanged();
    }

    private async Task FeatureWithAbsorbAsync(Guid id)
    {
        try
        {
            await JSRuntime.InvokeVoidAsync("absorbRecommendationRow", id, ".rec-featured");
        }
        catch
        {
            // Safe fallback — proceed with the state change even if the animation couldn't run
        }
        GoToSpotlight(id);
    }

    private async Task ArchiveAsync(BookmarkNodeDto item)
    {
        try
        {
            await BookmarkService.ArchiveBookmarkAsync(item.Id);
            RemoveRecommendation(item);
            Snackbar.Add($"Moved \"{item.Title}\" to Archive.", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to archive bookmark: {ex.Message}", Severity.Error);
        }
    }

    private async Task DeleteAsync(BookmarkNodeDto item)
    {
        try
        {
            if (await BookmarkService.DeleteBookmarkAsync(item.Id))
            {
                RemoveRecommendation(item);
                Snackbar.Add($"Deleted \"{item.Title}\".", Severity.Success);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to delete bookmark: {ex.Message}", Severity.Error);
        }
    }

    private void RemoveRecommendation(BookmarkNodeDto item)
    {
        var removedIndex = _recommendations.IndexOf(item);
        var wasSpotlighted = item.Id == _spotlightId;
        _recommendations.Remove(item);

        if (!wasSpotlighted) return;

        _spotlightId = _recommendations.Count == 0
            ? null
            : _recommendations[Math.Min(removedIndex, _recommendations.Count - 1)].Id;
    }

    private void GoToSpotlight(Guid id)
    {
        _spotlightId = id;
        StartSpotlightRotation();
    }

    private void NextSpotlight() => AdvanceSpotlight(1);

    private void PrevSpotlight() => AdvanceSpotlight(-1);

    private void AdvanceSpotlight(int delta)
    {
        if (_recommendations.Count == 0)
        {
            _spotlightId = null;
            return;
        }

        var count = _recommendations.Count;
        var baseIndex = _spotlightId is null ? 0 : Math.Max(0, _recommendations.FindIndex(r => r.Id == _spotlightId));
        var nextIndex = ((baseIndex + delta) % count + count) % count;
        _spotlightId = _recommendations[nextIndex].Id;
        StartSpotlightRotation();
    }

    private void PauseSpotlight() => _spotlightPaused = true;

    private void ResumeSpotlight() => _spotlightPaused = false;

    private void StartSpotlightRotation()
    {
        StopSpotlightRotation();
        if (_recommendations.Count <= 1) return;

        _spotlightCts = new CancellationTokenSource();
        _ = RunSpotlightRotationAsync(_spotlightCts.Token);
    }

    private void StopSpotlightRotation()
    {
        _spotlightCts?.Cancel();
        _spotlightCts?.Dispose();
        _spotlightCts = null;
    }

    private async Task RunSpotlightRotationAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SpotlightInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (_spotlightPaused || _recommendations.Count <= 1) continue;

                var count = _recommendations.Count;
                var baseIndex = _spotlightId is null ? 0 : Math.Max(0, _recommendations.FindIndex(r => r.Id == _spotlightId));
                _spotlightId = _recommendations[(baseIndex + 1) % count].Id;
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
            // rotation stopped
        }
    }

    public void Dispose()
    {
        StopSpotlightRotation();
        _dotNetRef?.Dispose();
    }
}
