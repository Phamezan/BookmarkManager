using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private Task LoadTagsAsync()
    {
        RecalculateAvailableTags();
        return Task.CompletedTask;
    }

    private void RecalculateAvailableTags()
    {
        if (!ShouldShowTagBar || _items == null)
        {
            _availableTags = [];
            return;
        }

        var query = _items.Where(i => i.Type == NodeType.Bookmark);

        if (_typeFilter == "Favorites")
        {
            query = query.Where(i => i.Metadata?.IsFavorite == true);
        }

        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            query = query.Where(i =>
                (i.Title != null && i.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)) ||
                (i.Url != null && i.Url.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)) ||
                (i.Metadata?.Tags != null && i.Metadata.Tags.Any(t => t.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)))
            );
        }

        if (_activeTagFilters.Count > 0)
        {
            query = query.Where(i =>
                i.Metadata != null && _activeTagFilters.All(f =>
                    (i.Metadata.Tags != null && i.Metadata.Tags.Contains(f, StringComparer.OrdinalIgnoreCase)) ||
                    (i.Metadata.Category != null && i.Metadata.Category.Equals(f, StringComparison.OrdinalIgnoreCase))
                )
            );
        }

        var matching = query.ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in matching)
        {
            if (item.Metadata?.Category != null)
            {
                var cat = item.Metadata.Category;
                counts[cat] = counts.TryGetValue(cat, out var c) ? c + 1 : 1;
            }

            if (item.Metadata?.Tags != null)
            {
                foreach (var tag in item.Metadata.Tags)
                {
                    counts[tag] = counts.TryGetValue(tag, out var c) ? c + 1 : 1;
                }
            }
        }

        _availableTags = counts
            .Select(kvp => new TagCountDto { Tag = kvp.Key, Count = kvp.Value })
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Tag)
            .ToList();
    }

    private bool IsTopLevelFolder(Guid folderId) => _folderTree.Any(folder => folder.Id == folderId);

    private static readonly HashSet<string> _exclusiveFormatTags = new(StringComparer.OrdinalIgnoreCase)
        { "Manga", "Manhwa", "Manhua" };

    private void ToggleTagFilter(string tag)
    {
        if (_activeTagFilters.Contains(tag))
        {
            _activeTagFilters.Remove(tag);
        }
        else
        {
            if (_exclusiveFormatTags.Contains(tag))
            {
                foreach (var t in _exclusiveFormatTags)
                    _activeTagFilters.Remove(t);
            }
            _activeTagFilters.Add(tag);
        }
    }

    private void ClearTagFilters()
    {
        _activeTagFilters.Clear();
    }

    private async Task OpenAutoTaggerDialog()
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.ExtraLarge, CloseButton = true };
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
