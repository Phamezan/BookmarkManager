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
        UndoService.Push(message, revertAction);
        Snackbar.Add(message, Severity.Success, config =>
        {
            config.Action = "UNDO";
            config.ActionColor = Color.Warning;
            config.OnClick = async snackbar =>
            {
                try
                {
                    await UndoService.UndoAsync();
                    if (_selectedFolderId.HasValue)
                        _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
                    await RefreshFolderTreeAsync();
                    Snackbar.Add("Action reverted", Severity.Success);
                }
                catch (Exception ex)
                {
                    Snackbar.Add($"Failed to undo action: {ex.Message}", Severity.Error);
                }
            };
        });
    }

}
