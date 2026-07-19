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

}
