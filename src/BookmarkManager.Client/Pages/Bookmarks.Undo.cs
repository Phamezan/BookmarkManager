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
                        if (_selectedFolderId.HasValue)
                            _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
                        await RefreshFolderTreeAsync();
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

}
