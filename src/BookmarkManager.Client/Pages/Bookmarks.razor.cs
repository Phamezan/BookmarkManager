using BookmarkManager.Client.Components;
using BookmarkManager.Client.Features.Bookmarks;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks : IDisposable
{
    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IExtensionConnectionService ExtensionConnectionService { get; set; } = default!;
    [Inject] private UndoService UndoService { get; set; } = default!;
    [Inject] private Microsoft.JSInterop.IJSRuntime JSRuntime { get; set; } = default!;

    private List<FolderTreeNodeDto> _folderTree = [];
    private List<BookmarkNodeDto> _items = [];
    private List<BookmarkNodeDto> _favorites = [];
    private HashSet<Guid> _expandedFolderIds = [];
    private bool _treeLoading = true;
    private bool _loading;
    private Guid? _selectedFolderId;
    /// <summary>Live card-grid column count reported by <c>BookmarkList</c> — drives Shift+H/L row-jump math (<c>Bookmarks.Keyboard.cs</c>).</summary>
    private int _gridColumns = 3;
    private Guid? _preSearchFolderId;
    private string _searchQuery = "";
    private readonly HashSet<Guid> _selectedBookmarkIds = [];
    private string _dragType = "";
    private Guid _draggedFolderId;
    private CancellationTokenSource? _wsCts;
    private bool _contextMenuOpen;
    private double _contextMenuX;
    private double _contextMenuY;
    private string _contextMenuType = "";
    private BookmarkNodeDto? _contextMenuBookmark;
    private Guid _contextMenuFolderId;
    private List<FolderTreeNodeDto> _contextSiblingFolders = [];
    private string _typeFilterBacking = "All";
    private string _typeFilter
    {
        get => _typeFilterBacking;
        set => _typeFilterBacking = value;
    }

    private HashSet<string> _activeTagFilters = [];
    private List<TagCountDto> _availableTags = [];
    /// <summary>Active URL-host filters — kept separate from <see cref="_activeTagFilters"/> so a
    /// host name (e.g. "action") never collides with a same-named genre tag (<c>Bookmarks.Tags.cs</c>).</summary>
    private HashSet<string> _activeHostFilters = [];
    private List<TagCountDto> _availableHosts = [];
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
    private int _lastVisibleActiveHostFiltersCount;
    private readonly HashSet<string> _lastVisibleActiveHostFilters = [];

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
                           || !_activeTagFilters.SetEquals(_lastVisibleActiveTagFilters)
                           || _activeHostFilters.Count != _lastVisibleActiveHostFiltersCount
                           || !_activeHostFilters.SetEquals(_lastVisibleActiveHostFilters);

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
                if (_activeTagFilters.Count > 0)
                {
                    items = items.Where(i =>
                        i.Metadata != null && _activeTagFilters.All(f =>
                            (i.Metadata.Tags != null && i.Metadata.Tags.Contains(f, StringComparer.OrdinalIgnoreCase)) ||
                            (i.Metadata.Category != null && i.Metadata.Category.Equals(f, StringComparison.OrdinalIgnoreCase))
                        )
                    );
                }

                if (_activeHostFilters.Count > 0)
                {
                    // Host filter is bookmark-only — folders are excluded from the result entirely.
                    items = items.Where(i =>
                        i.Type == NodeType.Bookmark
                        && BookmarkHostFilter.NormalizeHost(i.Url) is string host
                        && _activeHostFilters.Contains(host));
                }

                _cachedVisibleItems = BookmarkListOrdering.ApplySort(items, _sortMode).ToList();
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

                _lastVisibleActiveHostFiltersCount = _activeHostFilters.Count;

                _lastVisibleActiveHostFilters.Clear();
                foreach (var host in _activeHostFilters)
                {
                    _lastVisibleActiveHostFilters.Add(host);
                }
            }

            return _cachedVisibleItems!;
        }
    }

    private async Task OnFolderSelected(Guid folderId)
    {
        _selectedFolderId = folderId;
        _preSearchFolderId = folderId;
        _searchQuery = "";
        _loading = true;
        // Keyboard Enter navigates via JS interop — no automatic Blazor re-render
        // after the handler, so show loading immediately.
        await InvokeAsync(StateHasChanged);
        ClearAllFilters();
        try
        {
            _items = await BookmarkService.GetBookmarksAsync(folderId);
            await LoadTagsAsync();
            _focusedIndex = _items.Count > 0 ? 0 : -1;
            _selectedBookmarkIds.Clear();
            _lastSelectedId = null;
            _rangeSelectAnchorId = null;
            try
            {
                await JSRuntime.InvokeVoidAsync("bmPersistBookmarksFolder", folderId.ToString());
            }
            catch
            {
                // Older cached JS — ignore.
            }
        }
        catch (ApiException ex)
        {
            Snackbar.Add($"Failed to load bookmarks: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
            await InvokeAsync(StateHasChanged);
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
            ClearAllFilters();
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
        ClearAllFilters();
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

    private void OnGridColumnsChanged(int columns)
    {
        _gridColumns = Math.Clamp(columns, 1, 4);
    }

}
