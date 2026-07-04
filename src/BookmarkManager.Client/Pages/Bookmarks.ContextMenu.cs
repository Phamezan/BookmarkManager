using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
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
    }

    private void OnFolderContextMenu((Microsoft.AspNetCore.Components.Web.MouseEventArgs MouseEvent, Guid FolderId) args)
    {
        _contextMenuOpen = true;
        _contextMenuX = args.MouseEvent.ClientX;
        _contextMenuY = args.MouseEvent.ClientY;
        _contextMenuType = "folder";
        _contextMenuBookmark = null;
        _contextMenuFolderId = args.FolderId;
    }

    private void CloseContextMenu()
    {
        _contextMenuOpen = false;
        _contextMenuBookmark = null;
        _contextMenuFolderId = Guid.Empty;
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

    private async Task MoveContextFolder()
    {
        if (_contextMenuFolderId != Guid.Empty)
        {
            var id = _contextMenuFolderId;
            CloseContextMenu();
            await MoveFolder(id);
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

}
