using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private void ShowUndoSnackbar(string message, Func<Task> revertAction)
    {
        var action = UndoService.Push(message, revertAction);
        Snackbar.Add(message, Severity.Success, config =>
        {
            config.Action = "UNDO";
            config.ActionColor = Color.Warning;
            config.OnClick = async snackbar =>
            {
                try
                {
                    var undone = await UndoService.UndoAsync(action.Id);
                    if (undone)
                    {
                        await RefreshAfterUndoAsync();
                        Snackbar.Add("Action reverted", Severity.Success);
                    }
                    else
                    {
                        Snackbar.Add("Nothing to undo", Severity.Info);
                    }
                }
                catch (Exception ex)
                {
                    Snackbar.Add($"Failed to undo action: {ex.Message}", Severity.Error);
                }
            };
        });
    }

    /// <summary>
    /// Shared refresh after any successful undo — snackbar UNDO click and the
    /// global Ctrl+Z path (<see cref="HandleUndoShortcutAsync"/>) both call this
    /// so the two paths cannot drift.
    /// </summary>
    private async Task RefreshAfterUndoAsync()
    {
        if (_selectedFolderId.HasValue)
        {
            _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
            await LoadTagsAsync();
        }
        await RefreshFolderTreeAsync();
        StateHasChanged();
    }

    /// <summary>
    /// Global Ctrl+Z path (Phase 3) — registered under <c>BookmarksListContext</c>
    /// in <c>Bookmarks.Keyboard.cs</c> so it's only eligible while the Bookmarks
    /// page is mounted. Pops the newest action off <see cref="UndoService"/>
    /// regardless of which snackbar (if any) is currently showing.
    /// </summary>
    private async Task<bool> HandleUndoShortcutAsync()
    {
        try
        {
            var undone = await UndoService.UndoLatestAsync();
            if (!undone)
            {
                Snackbar.Add("Nothing to undo", Severity.Info);
                return true;
            }

            await RefreshAfterUndoAsync();
            Snackbar.Add("Action reverted", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to undo: {ex.Message}", Severity.Error);
        }
        return true;
    }
}
