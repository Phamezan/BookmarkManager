using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private async Task CreateFolder()
    {
        var parentId = _selectedFolderId ?? Guid.Empty;

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small };
        var dialog = await DialogService.ShowAsync<FolderCreateDialog>("Create Folder", options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not string folderName) return;

        await BookmarkService.CreateFolderAsync(parentId, folderName);

        _expandedFolderIds.Add(parentId);
        await RefreshFolderTreeAsync();
        StateHasChanged();
        Snackbar.Add("Folder created", Severity.Success);
    }

    private async Task CreateBookmark()
    {
        if (_selectedFolderId is null)
        {
            Snackbar.Add("Select a folder first", Severity.Warning);
            return;
        }

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium };
        var dialog = await DialogService.ShowAsync<BookmarkEditDialog>("Create Bookmark", options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not BookmarkEditDialog.BookmarkEditResult data) return;

        var created = await BookmarkService.CreateBookmarkAsync(_selectedFolderId.Value, data.Title, data.Url);
        if (data.Tags != null && data.Tags.Count > 0)
        {
            var metadata = new BookmarkMetadataDto { Tags = data.Tags };
            await BookmarkService.UpdateMetadataAsync(created.Id, metadata);
        }

        _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
        await LoadTagsAsync();
        await RefreshFolderTreeAsync();
        StateHasChanged();
        Snackbar.Add("Bookmark created", Severity.Success);
    }

    private async Task EditBookmark(BookmarkNodeDto item)
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium };
        var dialog = await DialogService.ShowAsync<BookmarkEditDialog>("Edit Bookmark", new DialogParameters { ["Node"] = item }, options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not BookmarkEditDialog.BookmarkEditResult data) return;

        await BookmarkService.UpdateBookmarkAsync(item.Id, data.Title, data.Url);
        var metadata = item.Metadata ?? new BookmarkMetadataDto();
        metadata.Tags = data.Tags;
        await BookmarkService.UpdateMetadataAsync(item.Id, metadata);

        if (_selectedFolderId.HasValue)
        {
            _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
            await LoadTagsAsync();
        }

        await RefreshFolderTreeAsync();
        StateHasChanged();
        Snackbar.Add("Bookmark updated", Severity.Success);
    }

    private async Task DeleteBookmark(BookmarkNodeDto item)
    {
        var dialog = await DialogService.ShowAsync<ConfirmDialog>("Delete Bookmark", new DialogParameters
        {
            ["Message"] = $"Delete \"{item.Title}\" from Brave after sync? Bookmark Manager keeps it recoverable for 30 days.",
            ["ConfirmText"] = "Delete",
            ["CancelText"] = "Cancel"
        });
        var result = await dialog.Result;
        if (result?.Canceled != false) return;

        await BookmarkService.DeleteBookmarkAsync(item.Id);
        _items.Remove(item);
        await LoadTagsAsync();
        await RefreshFolderTreeAsync();
        StateHasChanged();
        ShowUndoSnackbar($"Bookmark \"{item.Title}\" deleted", () => BookmarkService.RestoreBookmarkAsync(item.Id));
    }

    private async Task DeleteFolder(Guid folderId)
    {
        var folder = FindFolderById(_folderTree, folderId);
        if (folder is null) return;

        var dialog = await DialogService.ShowAsync<ConfirmDialog>("Delete Folder", new DialogParameters
        {
            ["Message"] = $"Delete folder \"{folder.Title}\" and all its contents from Brave after sync? Bookmark Manager keeps it recoverable for 30 days.",
            ["ConfirmText"] = "Delete",
            ["CancelText"] = "Cancel"
        });
        var result = await dialog.Result;
        if (result?.Canceled != false) return;

        await BookmarkService.DeleteBookmarkAsync(folderId);
    
        if (_selectedFolderId == folderId)
        {
            _selectedFolderId = null;
            _items = [];
        }

        await RefreshFolderTreeAsync();
        StateHasChanged();
        ShowUndoSnackbar($"Folder \"{folder.Title}\" deleted", () => BookmarkService.RestoreBookmarkAsync(folderId));
    }

    private async Task MoveBookmark(BookmarkNodeDto item)
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small };
        var parameters = new DialogParameters 
        { 
            ["Folders"] = _folderTree,
            ["CurrentFolderId"] = item.ParentId
        };
        var dialog = await DialogService.ShowAsync<MoveDialog>("Move Bookmark", parameters, options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not Guid targetFolderId) return;

        var originalParentId = item.ParentId;

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

    private async Task MoveFolder(Guid folderId)
    {
        var folder = FindFolderById(_folderTree, folderId);
        if (folder is null) return;

        var originalParentId = FindParentFolderId(_folderTree, folderId);

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small };
        var parameters = new DialogParameters 
        { 
            ["Folders"] = _folderTree,
            ["CurrentFolderId"] = originalParentId,
            ["FolderToMoveId"] = folderId
        };
        var dialog = await DialogService.ShowAsync<MoveDialog>("Move Folder", parameters, options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not Guid targetFolderId) return;

        await BookmarkService.MoveFolderAsync(folderId, targetFolderId);

        await RefreshFolderTreeAsync();
    
        if (originalParentId.HasValue)
        {
            StateHasChanged();
            ShowUndoSnackbar($"Folder \"{folder.Title}\" moved", () => BookmarkService.MoveFolderAsync(folderId, originalParentId.Value));
        }
        else
        {
            StateHasChanged();
            Snackbar.Add("Folder moved", Severity.Success);
        }
    }

    private async Task CreateBookmarkUnderFolder(Guid folderId)
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium };
        var dialog = await DialogService.ShowAsync<BookmarkEditDialog>("Create Bookmark", options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not BookmarkEditDialog.BookmarkEditResult data) return;

        var created = await BookmarkService.CreateBookmarkAsync(folderId, data.Title, data.Url);
        if (data.Tags != null && data.Tags.Count > 0)
        {
            var metadata = new BookmarkMetadataDto { Tags = data.Tags };
            await BookmarkService.UpdateMetadataAsync(created.Id, metadata);
        }

        if (_selectedFolderId == folderId)
        {
            _items = await BookmarkService.GetBookmarksAsync(folderId);
            await LoadTagsAsync();
        }
        await RefreshFolderTreeAsync();
        StateHasChanged();
        Snackbar.Add("Bookmark created", Severity.Success);
    }

    private void ShowMoveUnavailable(BookmarkNodeDto item)
        => Snackbar.Add($"Move picker for \"{item.Title}\" will use tracked folders only once the user bookmark API is available.", Severity.Info);

}
