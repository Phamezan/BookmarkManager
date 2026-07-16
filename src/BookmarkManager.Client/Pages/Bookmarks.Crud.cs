using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using BookmarkManager.Client.Extensions;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private async Task CreateFolder()
    {
        var parentId = _selectedFolderId ?? Guid.Empty;

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small, CloseOnEscapeKey = true };
        var dialog = await DialogService.ShowAsync<FolderCreateDialog>("Create Folder", options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not string folderName) return;

        try
        {
            await BookmarkService.CreateFolderAsync(parentId, folderName);
            _expandedFolderIds.Add(parentId);
            await RefreshFolderTreeAsync();
            StateHasChanged();
            Snackbar.Add("Folder created", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.AddApiError("create folder", ex);
        }
    }

    private async Task OnFolderCreated((Guid ParentId, string Name) args)
    {
        var (parentId, name) = args;
        try
        {
            await BookmarkService.CreateFolderAsync(parentId, name);
            _expandedFolderIds.Add(parentId);
            await RefreshFolderTreeAsync();
            StateHasChanged();
            Snackbar.Add("Folder created", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.AddApiError("create folder", ex);
            await RefreshFolderTreeAsync();
        }
    }

    private async Task CreateBookmark()
    {
        if (_selectedFolderId is null)
        {
            Snackbar.Add("Select a folder first", Severity.Warning);
            return;
        }

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium, CloseOnEscapeKey = true };
        var dialog = await DialogService.ShowAsync<BookmarkEditDialog>("Create Bookmark", options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not BookmarkEditDialog.BookmarkEditResult data) return;

        try
        {
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
        catch (Exception ex)
        {
            Snackbar.AddApiError("create bookmark", ex);
        }
    }

    private async Task EditBookmark(BookmarkNodeDto item)
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium, CloseOnEscapeKey = true };
        var dialog = await DialogService.ShowAsync<BookmarkEditDialog>("Edit Bookmark", new DialogParameters { ["Node"] = item }, options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not BookmarkEditDialog.BookmarkEditResult data) return;

        var originalTitle = item.Title;
        var originalUrl = item.Url;
        var titleOrUrlChanged = originalTitle != data.Title || originalUrl != data.Url;

        try
        {
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

            // Title/URL revert only — tag edits go through the auto-tagger's own
            // provenance path (Phase 5) and aren't covered by this undo entry.
            if (titleOrUrlChanged)
            {
                ShowUndoSnackbar("Bookmark updated", () => BookmarkService.UpdateBookmarkAsync(item.Id, originalTitle, originalUrl));
            }
            else
            {
                Snackbar.Add("Bookmark updated", Severity.Success);
            }
        }
        catch (Exception ex)
        {
            Snackbar.AddApiError("update bookmark", ex);
        }
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

        try
        {
            await BookmarkService.DeleteBookmarkAsync(item.Id);
            _items.Remove(item);
            await LoadTagsAsync();
            await RefreshFolderTreeAsync();
            StateHasChanged();
            ShowUndoSnackbar($"Bookmark \"{item.Title}\" deleted", () => BookmarkService.RestoreBookmarkAsync(item.Id));
        }
        catch (Exception ex)
        {
            Snackbar.AddApiError("delete bookmark", ex);
        }
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

        try
        {
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
        catch (Exception ex)
        {
            Snackbar.AddApiError("delete folder", ex);
        }
    }

    private async Task MoveBookmark(BookmarkNodeDto item)
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small, CloseOnEscapeKey = true };
        var parameters = new DialogParameters 
        { 
            ["Folders"] = _folderTree,
            ["CurrentFolderId"] = item.ParentId
        };
        var dialog = await DialogService.ShowAsync<MoveDialog>("Move Bookmark", parameters, options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not Guid targetFolderId) return;

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

    private async Task MoveFolder(Guid folderId)
    {
        var originalParentId = FindParentFolderId(_folderTree, folderId);

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Small, CloseOnEscapeKey = true };
        var parameters = new DialogParameters 
        { 
            ["Folders"] = _folderTree,
            ["CurrentFolderId"] = originalParentId,
            ["FolderToMoveId"] = folderId
        };
        var dialog = await DialogService.ShowAsync<MoveDialog>("Move Folder", parameters, options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not Guid targetFolderId) return;

        await MoveFolderWithUndoAsync(folderId, targetFolderId);
    }

    private async Task MoveFolderWithUndoAsync(Guid folderId, Guid targetFolderId)
    {
        if (folderId == targetFolderId) return;

        var folder = FindFolderById(_folderTree, folderId);
        if (folder is null) return;

        var originalParentId = FindParentFolderId(_folderTree, folderId);

        try
        {
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
        catch (Exception ex)
        {
            Snackbar.AddApiError("move folder", ex);
        }
    }

    private async Task CreateBookmarkUnderFolder(Guid folderId)
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium, CloseOnEscapeKey = true };
        var dialog = await DialogService.ShowAsync<BookmarkEditDialog>("Create Bookmark", options);
        var result = await dialog.Result;
        if (result?.Canceled != false || result.Data is not BookmarkEditDialog.BookmarkEditResult data) return;

        try
        {
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
        catch (Exception ex)
        {
            Snackbar.AddApiError("create bookmark", ex);
        }
    }


}

