using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    protected override async Task OnInitializedAsync()
    {
        ExtensionConnectionService.ConnectionStateChanged += OnConnectionStateChanged;
        if (ExtensionConnectionService.IsConnected)
        {
            await LoadDataAsync();
        }
        else
        {
            _treeLoading = false;
        }
    }

    private async Task LoadDataAsync()
    {
        _treeLoading = true;
        StateHasChanged();
        await LoadFavoritesAsync();
        _availableTags = [];
        try
        {
            _folderTree = await BookmarkService.GetFolderTreeAsync();
        }
        catch (ApiException ex)
        {
            Snackbar.Add($"Failed to load folders: {ex.Message}", Severity.Error);
        }
        finally
        {
            _treeLoading = false;
        }

        if (_folderTree.Count > 0)
        {
            var rootFolder = _folderTree.FirstOrDefault(f => f.Title.Equals("Bookmarks Bar", StringComparison.OrdinalIgnoreCase))
                             ?? _folderTree[0];
            if (rootFolder != null && rootFolder.Id != Guid.Empty)
            {
                await OnFolderSelected(rootFolder.Id);
            }
        }

        _wsCts?.Cancel();
        _wsCts?.Dispose();
        _wsCts = new CancellationTokenSource();
        _ = StartWebSocketListenerAsync(_wsCts.Token);
        StateHasChanged();
    }

    private void OnConnectionStateChanged()
    {
        InvokeAsync(async () =>
        {
            if (ExtensionConnectionService.IsConnected)
            {
                await LoadDataAsync();
            }
            else
            {
                _folderTree.Clear();
                _items.Clear();
                _favorites.Clear();
                _selectedFolderId = null;
                StateHasChanged();
            }
        });
    }

    public void Dispose()
    {
        ExtensionConnectionService.ConnectionStateChanged -= OnConnectionStateChanged;
        _wsCts?.Cancel();
        _wsCts?.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
    }
}
