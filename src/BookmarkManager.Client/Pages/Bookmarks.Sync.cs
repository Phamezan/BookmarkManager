using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    [Inject] private ILogger<Bookmarks> Logger { get; set; } = default!;
    [Inject] private SyncSocketListener SyncSocketListener { get; set; } = default!;

    private async Task StartWebSocketListenerAsync(CancellationToken ct)
    {
        await SyncSocketListener.ListenAsync(async () =>
        {
            await InvokeAsync(async () =>
            {
                try
                {
                    _folderTree = await BookmarkService.GetFolderTreeAsync(ct);
                    await LoadFavoritesAsync();

                    if (!string.IsNullOrWhiteSpace(_searchQuery))
                    {
                        _items = (await BookmarkService.SearchBookmarksAsync(new SearchRequest { Query = _searchQuery }, ct)).Items;
                        await LoadTagsAsync();
                        StateHasChanged();
                        return;
                    }

                    // If the selected folder no longer exists in the new tree (e.g. after a restore),
                    // fall back to the first available folder to avoid loading stale/empty data.
                    var allFolderIds = CollectAllFolderIds(_folderTree);
                    if (_selectedFolderId == null || !allFolderIds.Contains(_selectedFolderId.Value))
                    {
                        if (_folderTree.Count > 0)
                        {
                            var firstId = FindFirstLeaf(_folderTree[0]);
                            if (firstId != Guid.Empty)
                                await OnFolderSelected(firstId);
                        }
                        else
                        {
                            _selectedFolderId = null;
                            _items = [];
                        }
                    }
                    else
                    {
                        _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value, ct);
                        await LoadTagsAsync();
                    }
                    StateHasChanged();
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error refreshing data on sync broadcast");
                }
            });
        }, ct);
    }
}
