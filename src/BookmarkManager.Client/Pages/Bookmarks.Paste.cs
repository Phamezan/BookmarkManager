using BookmarkManager.Client.Extensions;
using BookmarkManager.Client.Features.Bookmarks;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

/// <summary>
/// Paste-URL-to-add (Phase 4 of <c>Docs/feature-plan-bookmarks-ux.md</c>).
/// Shared by the empty-area context menu's "Paste URL" item
/// (<c>Bookmarks.ContextMenu.cs</c>) and the Ctrl+V shortcut
/// (<c>Bookmarks.Keyboard.cs</c>) — one code path, no drift.
/// </summary>
public partial class Bookmarks
{
    private async Task PasteUrlAsBookmarkAsync()
    {
        if (_selectedFolderId is not Guid folderId)
        {
            Snackbar.Add("Select a folder first", Severity.Warning);
            return;
        }

        string? clipboardText;
        try
        {
            clipboardText = await JSRuntime.InvokeAsync<string>("readClipboardText", Array.Empty<object>());
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to read clipboard: {ex.Message}", Severity.Error);
            return;
        }

        if (!BookmarkUrlPaste.TryParseHttpUrl(clipboardText, out var url, out var error))
        {
            Snackbar.Add(error, Severity.Warning);
            return;
        }

        try
        {
            // No server-side title-fetch exists for arbitrary URLs — URL doubles as
            // the placeholder title until the user renames it (or Phase 6 lands).
            var created = await BookmarkService.CreateBookmarkAsync(folderId, url, url);

            if (_selectedFolderId == folderId)
            {
                _items = await BookmarkService.GetBookmarksAsync(folderId);
                await LoadTagsAsync();
            }
            await RefreshFolderTreeAsync();
            StateHasChanged();

            ShowUndoSnackbar($"Bookmark \"{url}\" added", () => BookmarkService.DeleteBookmarkAsync(created.Id));
        }
        catch (Exception ex)
        {
            Snackbar.AddApiError("paste bookmark", ex);
        }
    }
}
