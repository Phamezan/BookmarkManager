using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private async Task LoadTagsAsync()
    {
        if (!ShouldShowTagBar)
        {
            _availableTags = [];
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(_searchQuery))
            {
                _availableTags = _items
                    .Where(item => item.Metadata?.Tags != null)
                    .SelectMany(item => item.Metadata!.Tags)
                    .GroupBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new TagCountDto { Tag = group.Key, Count = group.Count() })
                    .OrderByDescending(t => t.Count)
                    .ThenBy(t => t.Tag)
                    .ToList();
            }
            else
            {
                _availableTags = await BookmarkService.GetTagsAsync(_selectedFolderId);
            }
        }
        catch
        {
            _availableTags = [];
        }
    }

    private bool IsTopLevelFolder(Guid folderId) => _folderTree.Any(folder => folder.Id == folderId);

    private void ToggleTagFilter(string tag)
    {
        if (!_activeTagFilters.Remove(tag))
            _activeTagFilters.Add(tag);
        _currentPage = 1;
    }

    private void ClearTagFilters()
    {
        if (_activeTagFilters.Count > 0)
        {
            _activeTagFilters.Clear();
            _currentPage = 1;
        }
    }

    private async Task OpenAutoTaggerDialog()
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium, CloseButton = true };
        var parameters = new DialogParameters<AutoTaggerDialog>
        {
            { dialog => dialog.CurrentFolderId, _selectedFolderId }
        };
        var dialog = await DialogService.ShowAsync<AutoTaggerDialog>("Auto Tagger", parameters, options);
        var result = await dialog.Result;
    
        await LoadTagsAsync();
        if (_selectedFolderId.HasValue)
        {
            _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
            await LoadTagsAsync();
        }
        StateHasChanged();
    }

    private List<TagGroup> GroupTags(List<TagCountDto> tags)
    {
        var groups = new List<TagGroup>();
    
        var mediumTags = tags.Where(t => TagCategorizer.GetCategory(t.Tag) == "Medium").ToList();
        var originTags = tags.Where(t => TagCategorizer.GetCategory(t.Tag) == "Origin").ToList();
        var genreTags = tags.Where(t => TagCategorizer.GetCategory(t.Tag) == "Genre").ToList();
        var otherTags = tags.Where(t => TagCategorizer.GetCategory(t.Tag) == "Other").Take(15).ToList();

        if (mediumTags.Count > 0)
            groups.Add(new TagGroup("Format", mediumTags));
        if (originTags.Count > 0)
            groups.Add(new TagGroup("Origin", originTags));
        if (genreTags.Count > 0)
            groups.Add(new TagGroup("Genres", genreTags));
        if (otherTags.Count > 0)
            groups.Add(new TagGroup("Tags", otherTags));

        return groups;
    }

    private Color GetCategoryColor(string categoryName)
    {
        return categoryName switch
        {
            "Format" => Color.Primary,
            "Origin" => Color.Secondary,
            "Genres" => Color.Info,
            _ => Color.Default
        };
    }

    public sealed record TagGroup(string CategoryName, List<TagCountDto> Tags);

    public static class TagCategorizer
    {
        private static readonly HashSet<string> MediumTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "Manga", "Manhwa", "Manhua", "Novel", "Artbook", "OEL", "Doujinshi", "Anime"
        };

        private static readonly HashSet<string> OriginTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "Japanese", "Korean", "Chinese"
        };

        private static readonly HashSet<string> GenreTags = new(StringComparer.OrdinalIgnoreCase)
        {
            "Action", "Adventure", "Comedy", "Drama", "Ecchi", "Fantasy", "Harem", "Historical",
            "Horror", "Mahou Shoujo", "Mecha", "Music", "Mystery", "Psychological", "Romance",
            "Sci-Fi", "Slice of Life", "Sports", "Supernatural", "Thriller", "Yaoi", "Yuri",
            "Shounen", "Shoujo", "Seinen", "Josei", "Gender Bender", "Tragedy", "School Life",
            "Martial Arts", "Wuxia", "Xianxia", "Xuanhuan", "Magic", "Isekai"
        };

        public static string GetCategory(string tag)
        {
            if (MediumTags.Contains(tag))
                return "Medium";
            if (OriginTags.Contains(tag))
                return "Origin";
            if (GenreTags.Contains(tag))
                return "Genre";
            return "Other";
        }
    }

}
