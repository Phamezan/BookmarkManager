using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private async Task ToggleFavorite(BookmarkNodeDto item)
    {
        var updatedMetadata = new BookmarkMetadataDto
        {
            Category = item.Metadata?.Category,
            Status = item.Metadata?.Status,
            CurrentProgress = item.Metadata?.CurrentProgress,
            TotalProgress = item.Metadata?.TotalProgress,
            Tags = item.Metadata?.Tags ?? [],
            Rating = item.Metadata?.Rating,
            Notes = item.Metadata?.Notes,
            IsFavorite = !item.Metadata?.IsFavorite ?? true,
            CoverImageUrl = item.Metadata?.CoverImageUrl
        };
    
        try
        {
            await BookmarkService.UpdateMetadataAsync(item.Id, updatedMetadata);
            await LoadFavoritesAsync();
            if (_selectedFolderId.HasValue)
            {
                _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
            }
            StateHasChanged();
            Snackbar.Add(updatedMetadata.IsFavorite ? $"Pinned \"{item.Title}\" to favorites" : $"Unpinned \"{item.Title}\" from favorites", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to toggle favorite: {ex.Message}", Severity.Error);
        }
    }

    private async Task LoadFavoritesAsync()
    {
        try
        {
            _favorites = await BookmarkService.GetFavoritesAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load favorites: {ex.Message}");
        }
    }

    private void OnFavoritesDragOver()
    {
        FavoritesDragOverStyle = "border: 2px dashed var(--bm-accent); background: rgba(59, 130, 246, 0.08) !important; border-radius: 8px;";
    }

    private void OnFavoritesDragLeave()
    {
        FavoritesDragOverStyle = "";
    }

    private async Task OnFavoritesDrop()
    {
        FavoritesDragOverStyle = "";
        try
        {
            if (_dragType == "folder" && _draggedFolderId != Guid.Empty)
            {
                var folderId = _draggedFolderId;
                _dragType = "";
                _draggedFolderId = Guid.Empty;
            
                var folder = await BookmarkService.GetBookmarkAsync(folderId);
                if (folder is not null && folder.Metadata?.IsFavorite != true)
                {
                    await ToggleFavorite(folder);
                }
            }
            else if (_selectedBookmarkIds.Count > 0)
            {
                foreach (var id in _selectedBookmarkIds)
                {
                    var item = await BookmarkService.GetBookmarkAsync(id);
                    if (item is not null && item.Metadata?.IsFavorite != true)
                    {
                        await ToggleFavorite(item);
                    }
                }
                _selectedBookmarkIds.Clear();
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to add shortcut: {ex.Message}", Severity.Error);
        }
    }

}
