using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks : IDisposable
{
    protected const string SortTitle = "Title";
    protected const string SortUpdated = "Updated";

    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IExtensionConnectionService ExtensionConnectionService { get; set; } = default!;
    [Inject] private UndoService UndoService { get; set; } = default!;

    private List<FolderTreeNodeDto> _folderTree = [];
    private List<BookmarkNodeDto> _items = [];
    private List<BookmarkNodeDto> _favorites = [];
    private HashSet<Guid> _expandedFolderIds = [];
    private bool _treeLoading = true;
    private bool _loading;
    private Guid? _selectedFolderId;
    private Guid? _preSearchFolderId;
    private string _searchQuery = "";
    private readonly HashSet<Guid> _selectedBookmarkIds = [];
    private string _dragType = "";
    private Guid _draggedFolderId;
    private string FavoritesDragOverStyle { get; set; } = "";
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _wsCts;
    private bool _contextMenuOpen;
    private double _contextMenuX;
    private double _contextMenuY;
    private string _contextMenuType = "";
    private BookmarkNodeDto? _contextMenuBookmark;
    private Guid _contextMenuFolderId;
    private string _typeFilterBacking = "All";
    private string _typeFilter
    {
        get => _typeFilterBacking;
        set => _typeFilterBacking = value;
    }

    private HashSet<string> _activeTagFilters = [];
    private List<TagCountDto> _availableTags = [];
    private bool _retagBusy;
    private bool ShouldShowTagBar => (_selectedFolderId.HasValue && !IsTopLevelFolder(_selectedFolderId.Value)) || !string.IsNullOrWhiteSpace(_searchQuery);
    private bool _tagsCollapsed = true;
    private readonly HashSet<string> _expandedCategories = [];

    private void ToggleCollapse() => _tagsCollapsed = !_tagsCollapsed;
    private void ToggleCategoryExpand(string category)
    {
        if (!_expandedCategories.Remove(category))
            _expandedCategories.Add(category);
    }

    private string _sortModeBacking = "UpdatedDesc";
    private string _sortMode
    {
        get => _sortModeBacking;
        set => _sortModeBacking = value;
    }

    private List<BookmarkNodeDto>? _cachedVisibleItems;
    private List<BookmarkNodeDto>? _lastVisibleSourceItems;
    private int _lastVisibleSourceItemsCount;
    private string? _lastVisibleTypeFilter;
    private string? _lastVisibleSortMode;
    private int _lastVisibleActiveTagFiltersCount;
    private readonly HashSet<string> _lastVisibleActiveTagFilters = [];

    protected List<BookmarkNodeDto> VisibleItems
    {
        get
        {
            bool isDirty = _cachedVisibleItems == null 
                           || _items != _lastVisibleSourceItems 
                           || _items.Count != _lastVisibleSourceItemsCount
                           || _typeFilter != _lastVisibleTypeFilter 
                           || _sortMode != _lastVisibleSortMode
                           || _activeTagFilters.Count != _lastVisibleActiveTagFiltersCount
                           || !_activeTagFilters.SetEquals(_lastVisibleActiveTagFilters);

            if (isDirty)
            {
                var items = _items.AsEnumerable();
                if (_typeFilter == "Folders")
                {
                    items = items.Where(i => i.Type == NodeType.Folder);
                }
                else if (_typeFilter == "Bookmarks")
                {
                    items = items.Where(i => i.Type == NodeType.Bookmark);
                }
                else if (_typeFilter == "Favorites")
                {
                    items = items.Where(i => i.Metadata?.IsFavorite == true);
                }
                else if (_typeFilter == "Behind")
                {
                    items = items.Where(i => i.ChaptersBehind > 0);
                }

                if (_activeTagFilters.Count > 0)
                {
                    items = items.Where(i =>
                        i.Metadata != null && _activeTagFilters.All(f =>
                            (i.Metadata.Tags != null && i.Metadata.Tags.Contains(f, StringComparer.OrdinalIgnoreCase)) ||
                            (i.Metadata.Category != null && i.Metadata.Category.Equals(f, StringComparison.OrdinalIgnoreCase))
                        )
                    );
                }

                var sorted = _sortMode switch
                {
                    "TitleDesc" => items.OrderByDescending(item => item.Metadata?.IsFavorite == true)
                                       .ThenByDescending(item => item.Title),
                    "UpdatedAsc" => items.OrderByDescending(item => item.Metadata?.IsFavorite == true)
                                        .ThenBy(item => item.UpdatedAt),
                    "UpdatedDesc" => items.OrderByDescending(item => item.Metadata?.IsFavorite == true)
                                         .ThenByDescending(item => item.UpdatedAt),
                    "Behind" => items.OrderByDescending(item => item.Metadata?.IsFavorite == true)
                                     .ThenByDescending(item => item.ChaptersBehind ?? 0)
                                     .ThenBy(item => item.Title),
                    _ => items.OrderByDescending(item => item.Metadata?.IsFavorite == true)
                              .ThenBy(item => item.Title)
                };

                _cachedVisibleItems = sorted.ToList();
                RecalculateAvailableTags(); // Recalculate dynamic tag counts

                _lastVisibleSourceItems = _items;
                _lastVisibleSourceItemsCount = _items.Count;
                _lastVisibleTypeFilter = _typeFilter;
                _lastVisibleSortMode = _sortMode;
                _lastVisibleActiveTagFiltersCount = _activeTagFilters.Count;
                
                _lastVisibleActiveTagFilters.Clear();
                foreach (var tag in _activeTagFilters)
                {
                    _lastVisibleActiveTagFilters.Add(tag);
                }
            }

            return _cachedVisibleItems!;
        }
    }

    private async Task OnFolderSelected(Guid folderId)
    {
        _debounceCts?.Cancel();
        _selectedFolderId = folderId;
        _preSearchFolderId = folderId;
        _searchQuery = "";
        _loading = true;
        ClearTagFilters();
        try
        {
            _items = await BookmarkService.GetBookmarksAsync(folderId);
            await LoadTagsAsync();
        }
        catch (ApiException ex)
        {
            Snackbar.Add($"Failed to load bookmarks: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task OnSearchChanged(string? value)
    {
        var oldQuery = _searchQuery;
        _searchQuery = value ?? "";
        _loading = true;

        if (string.IsNullOrWhiteSpace(oldQuery) && !string.IsNullOrWhiteSpace(_searchQuery) && _selectedFolderId.HasValue)
        {
            _preSearchFolderId = _selectedFolderId;
        }

        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            _selectedFolderId = _preSearchFolderId;
            ClearTagFilters();
            StateHasChanged();

            try
            {
                if (_selectedFolderId.HasValue)
                {
                    _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
                }
                else
                {
                    _items = [];
                }
                await LoadTagsAsync();
            }
            catch (ApiException ex)
            {
                Snackbar.Add($"Failed to load bookmarks: {ex.Message}", Severity.Error);
            }
            finally
            {
                _loading = false;
                StateHasChanged();
            }
            return;
        }

        _selectedFolderId = null;
        ClearTagFilters();
        StateHasChanged();

        try
        {
            _items = (await BookmarkService.SearchBookmarksAsync(new SearchRequest { Query = _searchQuery })).Items;
            await LoadTagsAsync();
        }
        catch (ApiException ex)
        {
            Snackbar.Add($"Search failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    private void OnSortModeChanged(string sortMode)
    {
        _sortMode = sortMode;
        StateHasChanged();
    }

    private void OnTypeFilterChanged(string typeFilter)
    {
        _typeFilter = typeFilter;
        StateHasChanged();
    }

    private async Task OnProgressUpdated()
    {
        if (_selectedFolderId.HasValue)
        {
            _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            _items = (await BookmarkService.SearchBookmarksAsync(new SearchRequest { Query = _searchQuery })).Items;
        }
        StateHasChanged();
    }
}
