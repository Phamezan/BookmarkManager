using BookmarkManager.Client.Components;
using BookmarkManager.Client.Extensions;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private void OnBookmarkContextMenu(Microsoft.AspNetCore.Components.Web.MouseEventArgs e, BookmarkNodeDto item)
    {
        _contextMenuOpen = true;
        _contextMenuX = e.ClientX;
        _contextMenuY = e.ClientY;
        _contextMenuType = "bookmark";
        _contextMenuBookmark = item;
        _contextMenuFolderId = Guid.Empty;
        _contextSiblingFolders = item.ParentId is Guid parentId
            ? GetFoldersInDirectory(parentId)
            : [];
        StateHasChanged();
    }

    /// <summary>
    /// Right-click on list background (not a card — <c>BookmarkCard</c> stops
    /// propagation on its own <c>@oncontextmenu</c>). Phase 4 empty-area menu.
    /// </summary>
    private void OnListBackgroundContextMenu(Microsoft.AspNetCore.Components.Web.MouseEventArgs e)
    {
        _contextMenuOpen = true;
        _contextMenuX = e.ClientX;
        _contextMenuY = e.ClientY;
        _contextMenuType = "empty";
        _contextMenuBookmark = null;
        _contextMenuFolderId = Guid.Empty;
        _contextSiblingFolders = [];
        StateHasChanged();
    }

    private async Task CreateBookmarkAtEmptyContext()
    {
        CloseContextMenu();
        if (_selectedFolderId is not Guid folderId)
        {
            Snackbar.Add("Select a folder first", Severity.Warning);
            return;
        }
        await CreateBookmarkUnderFolder(folderId);
    }

    private async Task CreateFolderAtEmptyContext()
    {
        CloseContextMenu();
        await CreateFolder();
    }

    private async Task PasteUrlAtEmptyContext()
    {
        CloseContextMenu();
        await PasteUrlAsBookmarkAsync();
    }

    private void OnFolderContextMenu((Microsoft.AspNetCore.Components.Web.MouseEventArgs MouseEvent, Guid FolderId) args)
    {
        _contextMenuOpen = true;
        _contextMenuX = args.MouseEvent.ClientX;
        _contextMenuY = args.MouseEvent.ClientY;
        _contextMenuType = "folder";
        _contextMenuBookmark = null;
        _contextMenuFolderId = args.FolderId;
        _contextSiblingFolders = GetFoldersInDirectoryForFolder(args.FolderId);
        StateHasChanged();
    }

    private void CloseContextMenu()
    {
        _contextMenuOpen = false;
        _contextMenuBookmark = null;
        _contextMenuFolderId = Guid.Empty;
        _contextSiblingFolders = [];
    }

    private List<FolderTreeNodeDto> GetFoldersInDirectoryForFolder(Guid folderId)
    {
        var directoryId = FindParentFolderId(_folderTree, folderId);
        if (directoryId is Guid dirId)
            return GetFoldersInDirectory(dirId, excludeFolderId: folderId);

        return _folderTree
            .Where(f => f.Id != folderId)
            .ToList();
    }

    /// <summary>
    /// Child folders inside <paramref name="directoryFolderId"/> (the directory the item lives in),
    /// ordered to match the current folder view when applicable.
    /// </summary>
    private List<FolderTreeNodeDto> GetFoldersInDirectory(Guid directoryFolderId, Guid? excludeFolderId = null)
    {
        var directory = FindFolderById(_folderTree, directoryFolderId);
        if (directory is null)
            return [];

        var candidates = directory.Children
            .Where(f => excludeFolderId is null || f.Id != excludeFolderId)
            .ToList();

        if (candidates.Count == 0)
            return [];

        if (_selectedFolderId != directoryFolderId)
            return candidates;

        var viewOrder = _items
            .Where(i => i.Type == NodeType.Folder)
            .Select((item, index) => (item.Id, index))
            .ToDictionary(x => x.Id, x => x.index);

        return candidates
            .OrderBy(f => viewOrder.TryGetValue(f.Id, out var idx) ? idx : int.MaxValue)
            .ToList();
    }

    private async Task EditContextBookmark()
    {
        if (_contextMenuBookmark is not null)
        {
            var item = _contextMenuBookmark;
            CloseContextMenu();
            await EditBookmark(item);
        }
    }

    private async Task MoveContextBookmark()
    {
        if (_contextMenuBookmark is not null)
        {
            var item = _contextMenuBookmark;
            CloseContextMenu();
            await MoveBookmark(item);
        }
    }

    private async Task MoveContextBookmarkToFolder(Guid targetFolderId)
    {
        if (_contextMenuBookmark is not null)
        {
            var item = _contextMenuBookmark;
            CloseContextMenu();
            await MoveBookmarkToFolderAsync(item, targetFolderId);
        }
    }

    private async Task MoveContextFolder()
    {
        if (_contextMenuFolderId != Guid.Empty)
        {
            var id = _contextMenuFolderId;
            CloseContextMenu();
            await MoveFolder(id);
        }
    }

    private async Task MoveContextFolderToSibling(Guid targetFolderId)
    {
        if (_contextMenuFolderId != Guid.Empty)
        {
            var id = _contextMenuFolderId;
            CloseContextMenu();
            await MoveFolderWithUndoAsync(id, targetFolderId);
        }
    }

    private async Task CreateBookmarkInContextFolder()
    {
        if (_contextMenuFolderId != Guid.Empty)
        {
            var id = _contextMenuFolderId;
            CloseContextMenu();
            await CreateBookmarkUnderFolder(id);
        }
    }

    private async Task DeleteContextBookmark()
    {
        if (_contextMenuBookmark is not null)
        {
            var item = _contextMenuBookmark;
            CloseContextMenu();
            await DeleteBookmark(item);
        }
    }

    private async Task DeleteContextFolder()
    {
        if (_contextMenuFolderId != Guid.Empty)
        {
            var id = _contextMenuFolderId;
            CloseContextMenu();
            await DeleteFolder(id);
        }
    }

    private async Task ToggleFavoriteContextBookmark()
    {
        if (_contextMenuBookmark is not null)
        {
            var item = _contextMenuBookmark;
            CloseContextMenu();
            await ToggleFavorite(item);
        }
    }

    private async Task ToggleFavoriteContextFolder()
    {
        if (_contextMenuFolderId != Guid.Empty)
        {
            var id = _contextMenuFolderId;
            CloseContextMenu();
            try
            {
                var folder = await BookmarkService.GetBookmarkAsync(id);
                if (folder is not null)
                {
                    await ToggleFavorite(folder);
                }
            }
            catch (Exception ex)
            {
                Snackbar.Add($"Failed to toggle folder favorite: {ex.Message}", Severity.Error);
            }
        }
    }

    private async Task OpenContextBookmarkInNewTab()
    {
        if (_contextMenuBookmark is not { Url: { } url })
            return;

        CloseContextMenu();
        await JSRuntime.InvokeVoidAsync("openInNewTab", url);
    }

    private async Task CopyContextBookmarkUrl()
    {
        if (_contextMenuBookmark is not { Url: { } url })
            return;

        CloseContextMenu();
        try
        {
            await JSRuntime.InvokeVoidAsync("copyToClipboard", url);
            Snackbar.Add("URL copied to clipboard", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to copy URL: {ex.Message}", Severity.Error);
        }
    }

    private async Task GoToContextBookmarkFolder()
    {
        if (_contextMenuBookmark?.ParentId is not Guid parentId)
            return;

        CloseContextMenu();
        await OnFolderSelected(parentId);
    }

    private async Task OpenContextFolder()
    {
        if (_contextMenuFolderId == Guid.Empty)
            return;

        var id = _contextMenuFolderId;
        CloseContextMenu();
        await OnFolderSelected(id);
    }

    private async Task ArchiveContextBookmark()
    {
        if (_contextMenuBookmark is not { } item)
            return;

        CloseContextMenu();
        try
        {
            await BookmarkService.ArchiveBookmarkAsync(item.Id);
            if (_selectedFolderId.HasValue)
            {
                _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
                await LoadTagsAsync();
            }
            await RefreshFolderTreeAsync();
            StateHasChanged();
            Snackbar.Add($"Moved \"{item.Title}\" to Archive", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to archive bookmark: {ex.Message}", Severity.Error);
        }
    }

    private async Task MoveBookmarkToFolderAsync(BookmarkNodeDto item, Guid targetFolderId)
    {
        if (item.ParentId == targetFolderId)
            return;

        var originalParentId = item.ParentId;

        try
        {
            await BookmarkService.MoveBookmarkAsync(item.Id, targetFolderId);

            if (_selectedFolderId.HasValue)
            {
                _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
                await LoadTagsAsync();
            }

            await RefreshFolderTreeAsync();

            if (originalParentId.HasValue)
            {
                StateHasChanged();
                ShowUndoSnackbar($"Bookmark \"{item.Title}\" moved", () => BookmarkService.MoveBookmarkAsync(item.Id, originalParentId.Value));
            }
            else
            {
                StateHasChanged();
                Snackbar.Add("Bookmark moved", Severity.Success);
            }
        }
        catch (Exception ex)
        {
            Snackbar.AddApiError("move bookmark", ex);
        }
    }

}
