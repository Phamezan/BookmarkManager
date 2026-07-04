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
        set
        {
            if (_typeFilterBacking != value)
            {
                _typeFilterBacking = value;
                _currentPage = 1;
            }
        }
    }

    private HashSet<string> _activeTagFilters = [];
    private List<TagCountDto> _availableTags = [];
    private bool _retagBusy;
    private bool ShouldShowTagBar => (_selectedFolderId.HasValue && !IsTopLevelFolder(_selectedFolderId.Value)) || !string.IsNullOrWhiteSpace(_searchQuery);

    private string _sortModeBacking = "TitleAsc";
    private string _sortMode
    {
        get => _sortModeBacking;
        set
        {
            if (_sortModeBacking != value)
            {
                _sortModeBacking = value;
                _currentPage = 1;
            }
        }
    }

    private int _currentPage = 1;
    private const int PageSize = 50;

    protected List<BookmarkNodeDto> FilteredItems
    {
        get
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
                    i.Metadata?.Tags != null
                    && _activeTagFilters.All(f =>
                        i.Metadata.Tags.Contains(f, StringComparer.OrdinalIgnoreCase)));
            }

            return items.ToList();
        }
    }

    protected List<BookmarkNodeDto> VisibleItems => _sortMode switch
    {
        "TitleDesc" => FilteredItems.OrderByDescending(item => item.Title).ToList(),
        "UpdatedAsc" => FilteredItems.OrderBy(item => item.UpdatedAt).ToList(),
        "UpdatedDesc" => FilteredItems.OrderByDescending(item => item.UpdatedAt).ToList(),
        _ => FilteredItems.OrderBy(item => item.Title).ToList()
    };

    protected List<BookmarkNodeDto> PaginatedItems => VisibleItems.Skip((_currentPage - 1) * PageSize).Take(PageSize).ToList();
    protected int PageCount => (int)Math.Ceiling((double)VisibleItems.Count / PageSize);

    private void OnPageChanged(int page)
    {
        _currentPage = page;
    }

    private async Task OnFolderSelected(Guid folderId)
    {
        _currentPage = 1;
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
        _currentPage = 1;
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

}
