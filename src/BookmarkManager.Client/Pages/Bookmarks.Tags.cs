using BookmarkManager.Client.Components;
using BookmarkManager.Client.Features.Bookmarks;
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
            _availableHosts = [];
            return;
        }

        var query = _items.Where(i => i.Type == NodeType.Bookmark);

        if (_typeFilter == "Favorites")
        {
            query = query.Where(i => i.Metadata?.IsFavorite == true);
        }
        else if (_typeFilter == "Later")
        {
            query = query.Where(i =>
                string.Equals(i.Metadata?.Status, BookmarkReadingStatus.PlanToRead, StringComparison.Ordinal));
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

        if (_activeHostFilters.Count > 0)
        {
            query = query.Where(i =>
                BookmarkHostFilter.NormalizeHost(i.Url) is string host && _activeHostFilters.Contains(host));
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

        _availableHosts = BookmarkHostFilter.CountHosts(matching);
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

    private void ToggleHostFilter(string host)
    {
        if (!_activeHostFilters.Remove(host))
        {
            _activeHostFilters.Add(host);
        }
    }

    /// <summary>Clears both tag and host filters — background click, folder change, search
    /// clear, and the tag bar's "Clear All" button all need both cleared together.</summary>
    private void ClearAllFilters()
    {
        _activeTagFilters.Clear();
        _activeHostFilters.Clear();
    }

    private async Task OpenAutoTaggerDialog()
    {
        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.ExtraLarge, CloseButton = true, CloseOnEscapeKey = true };
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

    private List<TagGroup> GroupTags(List<TagCountDto> tags, List<TagCountDto> hosts)
    {
        var groups = new List<TagGroup>();

        // URL host chips render first — a distinct axis from tag/genre facets, never
        // sharing a namespace with them (see _activeHostFilters comment in Bookmarks.razor.cs).
        if (hosts.Count > 0)
            groups.Add(new TagGroup("URL", hosts, IsHostGroup: true));

        var mediumTags = tags.Where(t => TagCategorizer.GetCategory(t.Tag) == "Medium").ToList();
        var originTags = tags.Where(t => TagCategorizer.GetCategory(t.Tag) == "Origin").ToList();
        var genreTags = tags.Where(t => TagCategorizer.GetCategory(t.Tag) == "Genre").ToList();
        var otherTags = tags.Where(t => TagCategorizer.GetCategory(t.Tag) == "Other").ToList();

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

    public sealed record TagGroup(string CategoryName, List<TagCountDto> Tags, bool IsHostGroup = false);

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
