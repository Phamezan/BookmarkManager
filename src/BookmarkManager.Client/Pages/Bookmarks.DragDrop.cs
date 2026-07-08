using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private void OnFolderDragStart(Guid id)
    {
        _dragType = "folder";
        _draggedFolderId = id;
    }

    private async Task OnFolderDrop(Guid targetFolderId)
    {
        if (_dragType == "folder")
        {
            await MoveDraggedFolder(targetFolderId);
            _dragType = "";
        }
        else
        {
            await MoveSelectedBookmarks(targetFolderId);
        }
    }

    private async Task MoveDraggedFolder(Guid targetFolderId)
    {
        var draggedId = _draggedFolderId;
        _draggedFolderId = Guid.Empty;
        await MoveFolderWithUndoAsync(draggedId, targetFolderId);
    }

    private async Task MoveSelected()
    {
        if (_selectedBookmarkIds.Count == 0) return;

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small };
        var parameters = new DialogParameters
        {
            ["Folders"] = _folderTree,
            ["CurrentFolderId"] = _selectedFolderId
        };
        var dialog = await DialogService.ShowAsync<MoveDialog>("Move Selected Items", parameters, options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not Guid targetFolderId) return;

        await MoveSelectedBookmarks(targetFolderId);
    }

    private async Task DeleteSelected()
    {
        if (_selectedBookmarkIds.Count == 0) return;

        var dialog = await DialogService.ShowAsync<ConfirmDialog>("Delete Selected", new DialogParameters
        {
            ["Message"] = $"Delete {_selectedBookmarkIds.Count} selected bookmark(s) from Brave after sync? Bookmark Manager keeps them recoverable for 30 days.",
            ["ConfirmText"] = "Delete",
            ["CancelText"] = "Cancel"
        });
        var result = await dialog.Result;
        if (result?.Canceled != false) return;

        var idsToDelete = _selectedBookmarkIds.ToList();
        await BookmarkService.BatchDeleteBookmarksAsync(idsToDelete);

        _items.RemoveAll(i => idsToDelete.Contains(i.Id));
        _selectedBookmarkIds.Clear();
        await LoadTagsAsync();

        await RefreshFolderTreeAsync();
        ShowUndoSnackbar($"Deleted {idsToDelete.Count} bookmarks", async () =>
        {
            foreach (var id in idsToDelete)
            {
                await BookmarkService.RestoreBookmarkAsync(id);
            }
        });
    }

    private async Task MoveSelectedBookmarks(Guid targetFolderId)
    {
        if (_selectedBookmarkIds.Count == 0) return;

        var originalParents = new Dictionary<Guid, Guid>();
        foreach (var id in _selectedBookmarkIds)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item?.ParentId != null)
            {
                originalParents[id] = item.ParentId.Value;
            }
        }

        var count = 0;
        foreach (var id in _selectedBookmarkIds)
        {
            var result = await BookmarkService.MoveBookmarkAsync(id, targetFolderId);
            if (result != null) count++;
        }

        _selectedBookmarkIds.Clear();

        if (_selectedFolderId.HasValue)
        {
            _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
            await LoadTagsAsync();
        }

        await RefreshFolderTreeAsync();
        ShowUndoSnackbar($"Moved {count} bookmark(s)", async () =>
        {
            foreach (var kvp in originalParents)
            {
                await BookmarkService.MoveBookmarkAsync(kvp.Key, kvp.Value);
            }
        });
    }

}
