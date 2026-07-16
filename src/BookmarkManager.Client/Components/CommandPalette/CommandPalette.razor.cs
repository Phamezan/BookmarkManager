using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace BookmarkManager.Client.Components.CommandPalette;

public partial class CommandPalette : IDisposable
{
    private sealed class FolderSearchResult
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    private sealed class PaletteItem
    {
        public int Index { get; set; }
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public MarkupString TitleHtml { get; set; }
        public string Subtitle { get; set; } = string.Empty;
        public string? Category { get; set; }
        public string? Url { get; set; }
        public bool IsFolder { get; set; }
        public bool IsSectionHeader { get; set; }
        public string? SectionTitle { get; set; }
        public string? Tooltip { get; set; }
        public BookmarkNodeDto? BookmarkNode { get; set; }
    }

    /// <summary>
    /// True when the palette runs inside the extension's in-tab iframe. All
    /// actions then travel via postMessage to the extension host frame instead
    /// of navigating or opening windows inside the iframe document.
    /// </summary>
    [Parameter] public bool Embedded { get; set; }

    /// <summary>URL of the tab the embedded palette overlays.</summary>
    [Parameter] public string? ContextUrl { get; set; }

    private const int DefaultPageSize = 10;
    private const int SearchPageSize = 20;
    /// <summary>
    /// Fixed row stride for Virtualize + scrollPaletteToIndex.
    /// Must stay in sync with --palette-item-stride on #paletteList (height + margin-bottom).
    /// </summary>
    private const int ItemSizePx = 52;

    private string _searchQuery = string.Empty;
    private List<PaletteItem> _results = [];
    private int _selectedIndex = 0;
    private bool _triggerStagger = false;
    private string _primaryHint = "Go to Bookmark";
    private string _secondaryHint = "Open in New Tab";
    private string _tertiaryHint = "Go to Bookmark";
    private Dictionary<Guid, string> _folderPathById = new();

    private DotNetObjectReference<CommandPalette>? _dotNetRef;
    private CancellationTokenSource? _searchCts;
    private Guid? _filterFolderId;
    private IDisposable? _ctrlPRegistration;

    // Load-more paging state: tracks the request shape of the currently-loaded page so
    // "Load more" can fetch the next page with the same query/folder filter and append.
    private int _loadedPage = 1;
    private int _loadedPageSize = DefaultPageSize;
    private int _totalResultCount;
    private string _loadedQuery = string.Empty;
    private Guid? _loadedFolderId;
    private bool _isLoadingMore;
    /// <summary>Effective bookmark query used for title highlighting (empty for default results / folder autocomplete).</summary>
    private string _highlightQuery = string.Empty;
    /// <summary>Bookmark rows loaded for the current page sequence (excludes section headers / recent section).</summary>
    private int _loadedBookmarkCount;
    private int? _historyIndex;
    private IReadOnlyList<string> _searchHistory = [];
    private bool _hasSearchHistory;

    private bool HasMoreResults => !_isLoadingMore && _loadedBookmarkCount < _totalResultCount;

    [Inject] private KeyboardShortcutService KeyboardShortcutService { get; set; } = default!;

    protected override void OnInitialized()
    {
        PaletteService.OnToggle += OnToggle;
        NavigationManager.LocationChanged += OnLocationChanged;
        UpdateHints();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            await JSRuntime.InvokeVoidAsync("initializeCommandPalette", _dotNetRef);

            // Ctrl+P moved off the ad-hoc listener in command-palette.js and onto the
            // shared KeyboardShortcutService registry (context "global" — always eligible).
            await KeyboardShortcutService.EnsureInitializedAsync(JSRuntime);
            _ctrlPRegistration = KeyboardShortcutService.Register(
                "p", ctrl: true, shift: false, alt: false, KeyboardShortcutService.GlobalContext,
                () =>
                {
                    PaletteService.Toggle();
                    return Task.FromResult(true);
                });
        }
    }

    private void OnToggle()
    {
        if (PaletteService.IsOpen)
        {
            _searchQuery = string.Empty;
            _selectedIndex = 0;
            _filterFolderId = null;
            _results.Clear();
            _totalResultCount = 0;
            _triggerStagger = true;
            _historyIndex = null;
            UpdateHints();
            _ = RefreshSearchHistoryAsync();
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            _ = LoadDefaultResultsAsync(_searchCts.Token);
            _ = JSRuntime.InvokeVoidAsync("focusPaletteInput");
        }
        else if (Embedded)
        {
            // Tell the extension host frame to hide the overlay iframe.
            _ = JSRuntime.InvokeVoidAsync("paletteEmbedded.close");
        }
        StateHasChanged();
    }

    private async Task RefreshSearchHistoryAsync()
    {
        try
        {
            _searchHistory = await SearchHistoryService.GetAsync();
            _hasSearchHistory = _searchHistory.Count > 0;
            StateHasChanged();
        }
        catch
        {
            _searchHistory = [];
            _hasSearchHistory = false;
        }
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        UpdateHints();
        PaletteService.Close();
        StateHasChanged();
    }

    private void UpdateHints()
    {
        if (Embedded)
        {
            _primaryHint = IsContextOnManager ? "Go to Bookmark" : "Open Here";
            _secondaryHint = "Open in New Tab";
            _tertiaryHint = "Open in Manager";
            return;
        }

        var relativeUri = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var currentRoute = "/" + relativeUri.Split('?')[0];

        if (currentRoute.StartsWith("/library", StringComparison.OrdinalIgnoreCase))
        {
            _primaryHint = "Open in New Tab";
            _secondaryHint = "Go to Bookmark";
        }
        else
        {
            _primaryHint = "Go to Bookmark";
            _secondaryHint = "Open in New Tab";
        }

        // Same key chord as the extension overlay — tertiary is already GoToBookmark.
        _tertiaryHint = "Go to Bookmark";
    }

    private async Task LoadDefaultResultsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var folderTree = await BookmarkService.GetFolderTreeAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            RebuildFolderPathMap(folderTree);

            var candidates = await FrecencyService.GetTopAsync(PaletteFrecencyService.RecentSectionSize * 2);
            if (cancellationToken.IsCancellationRequested) return;

            var recent = new List<(Guid Id, PaletteFrecencyService.Entry Snapshot)>();
            foreach (var (id, snap) in candidates)
            {
                if (cancellationToken.IsCancellationRequested) return;
                var live = await BookmarkService.GetBookmarkAsync(id, cancellationToken);
                if (live is null || live.IsDeleted)
                {
                    await FrecencyService.RemoveAsync(id);
                    continue;
                }

                // Refresh snapshot fields from live node so the section stays accurate.
                snap.Title = live.Title;
                snap.Url = live.Url ?? snap.Url;
                snap.Category = live.Metadata?.Category ?? snap.Category;
                recent.Add((id, snap));
                if (recent.Count >= PaletteFrecencyService.RecentSectionSize)
                    break;
            }

            var recentIds = recent.Select(r => r.Id).ToHashSet();

            var request = new SearchRequest
            {
                Query = string.Empty,
                Page = 1,
                PageSize = DefaultPageSize
            };
            var pagedResult = await BookmarkService.SearchBookmarksAsync(request, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            var allBookmarks = pagedResult.Items?.ToList() ?? [];
            var sectionB = allBookmarks.Where(b => !recentIds.Contains(b.Id)).Select(MapBookmarkToItem).ToList();

            _highlightQuery = string.Empty;
            var items = new List<PaletteItem>();

            if (recent.Count > 0)
            {
                items.Add(CreateSectionHeader("Recently opened"));
                foreach (var (id, snap) in recent)
                {
                    items.Add(MapFrecencySnapshotToItem(id, snap));
                }

                items.Add(CreateSectionHeader("All bookmarks"));
            }

            items.AddRange(sectionB);

            if (cancellationToken.IsCancellationRequested) return;

            _results = items;
            AssignResultIndices();
            _selectedIndex = FirstSelectableIndex();
            _loadedBookmarkCount = allBookmarks.Count;
            RememberLoadedPage(request, pagedResult.TotalCount);
            StateHasChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Fail silently
        }
    }

    private static PaletteItem CreateSectionHeader(string title) => new()
    {
        IsSectionHeader = true,
        SectionTitle = title,
        Title = title,
        TitleHtml = new MarkupString(System.Net.WebUtility.HtmlEncode(title))
    };

    private PaletteItem MapFrecencySnapshotToItem(Guid id, PaletteFrecencyService.Entry snap)
    {
        string? folderPath = null;
        // Snapshots may lack ParentId — subtitle is host-only from stored URL.
        return new PaletteItem
        {
            Id = id,
            Title = snap.Title,
            TitleHtml = PaletteTitleHighlighter.BuildTitleHtml(snap.Title, string.Empty),
            Subtitle = FormatBookmarkSubtitle(folderPath, snap.Url),
            Category = snap.Category,
            Url = string.IsNullOrWhiteSpace(snap.Url) ? null : snap.Url,
            IsFolder = false,
            Tooltip = BuildItemTooltip(snap.Title, null)
        };
    }

    /// <summary>Records the request shape + total count for a just-loaded first page so "Load more" can continue it.</summary>
    private void RememberLoadedPage(SearchRequest request, int totalCount)
    {
        _loadedPage = request.Page;
        _loadedPageSize = request.PageSize;
        _loadedQuery = request.Query;
        _loadedFolderId = request.FolderId;
        _totalResultCount = totalCount;
    }

    /// <summary>
    /// Fetches the next page (same query/folder filter as the currently-loaded results) and
    /// appends it. The palette caps each request at <see cref="SearchPageSize"/>/<see
    /// cref="DefaultPageSize"/> results, so without this, results beyond that page were
    /// silently unreachable — this is what "Load more" in the palette footer calls.
    /// </summary>
    private async Task LoadMoreResultsAsync()
    {
        if (_isLoadingMore || _loadedBookmarkCount >= _totalResultCount) return;

        _isLoadingMore = true;
        StateHasChanged();

        try
        {
            var request = new SearchRequest
            {
                Query = _loadedQuery,
                Page = _loadedPage + 1,
                PageSize = _loadedPageSize,
                FolderId = _loadedFolderId
            };
            var pagedResult = await BookmarkService.SearchBookmarksAsync(request);
            // Prefer ids already present so load-more doesn't duplicate recent or prior pages.
            var existingIds = _results.Where(r => !r.IsSectionHeader).Select(r => r.Id).ToHashSet();

            var newItems = pagedResult.Items?
                .Where(b => !existingIds.Contains(b.Id))
                .Select(MapBookmarkToItem)
                .ToList() ?? [];
            _results.AddRange(newItems);
            AssignResultIndices();
            _loadedPage = request.Page;
            _loadedBookmarkCount += pagedResult.Items?.Count ?? 0;
            _totalResultCount = pagedResult.TotalCount;
            StateHasChanged();
        }
        catch
        {
            // Fail silently — the footer row simply stays clickable to retry.
        }
        finally
        {
            _isLoadingMore = false;
            StateHasChanged();
        }
    }

    private async Task HandleSearchInput(ChangeEventArgs e)
    {
        _historyIndex = null;
        await RunSearchAsync(e.Value?.ToString() ?? string.Empty, debounce: true);
    }

    private async Task RunSearchAsync(string query, bool debounce)
    {
        _searchQuery = query;
        _selectedIndex = 0;
        _triggerStagger = false;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        if (string.IsNullOrEmpty(_searchQuery))
        {
            _filterFolderId = null;
            await LoadDefaultResultsAsync(token);
            return;
        }

        var folderTree = await BookmarkService.GetFolderTreeAsync(token);
        RebuildFolderPathMap(folderTree);

        // 1. Folder Autocomplete Mode: starts with ">" and doesn't contain a space
        if (_searchQuery.StartsWith(">") && !_searchQuery.Contains(" "))
        {
            _filterFolderId = null;
            var folderQuery = _searchQuery.Substring(1).Trim();

            var folderMatches = new List<FolderSearchResult>();
            FindFoldersRecursive(folderTree, folderQuery, string.Empty, folderMatches);

            _results = folderMatches.Select(MapFolderToItem).ToList();
            // Folder autocomplete returns everything in one shot — no paging, so "Load more" never shows.
            _highlightQuery = string.Empty;
            AssignResultIndices();
            _totalResultCount = _results.Count;
            _loadedBookmarkCount = _results.Count;
            StateHasChanged();
            return;
        }

        // 2. Bookmark Search Mode (potentially folder-filtered with ">FolderName")
        Guid? activeFolderId = null;
        var bookmarkQuery = _searchQuery;

        if (_searchQuery.StartsWith(">"))
        {
            string? activeFolderName = null;
            if (_filterFolderId.HasValue)
            {
                var folder = FindFolderById(folderTree, _filterFolderId.Value);
                if (folder != null && _searchQuery.StartsWith($">{folder.Title}", StringComparison.OrdinalIgnoreCase))
                {
                    activeFolderName = folder.Title;
                    activeFolderId = _filterFolderId;
                }
            }

            if (activeFolderName == null)
            {
                var spaceIndex = _searchQuery.IndexOf(' ');
                if (spaceIndex > 1)
                {
                    var parsedFolderName = _searchQuery.Substring(1, spaceIndex - 1).Trim();
                    _filterFolderId = FindFolderIdByName(folderTree, parsedFolderName);
                    if (_filterFolderId.HasValue)
                    {
                        activeFolderName = parsedFolderName;
                        activeFolderId = _filterFolderId;
                    }
                }
                else
                {
                    var parsedFolderName = _searchQuery.Substring(1).Trim();
                    _filterFolderId = FindFolderIdByName(folderTree, parsedFolderName);
                    if (_filterFolderId.HasValue)
                    {
                        activeFolderName = parsedFolderName;
                        activeFolderId = _filterFolderId;
                    }
                }
            }

            if (activeFolderName != null)
            {
                var prefixLength = activeFolderName.Length + 1; // ">FolderName"
                if (_searchQuery.Length > prefixLength && _searchQuery[prefixLength] == ' ')
                {
                    prefixLength++;
                }
                bookmarkQuery = _searchQuery.Substring(prefixLength);
            }
        }
        else
        {
            _filterFolderId = null;
        }

        try
        {
            if (debounce)
                await Task.Delay(200, token); // Debounce

            var request = new SearchRequest
            {
                Query = bookmarkQuery.Trim(),
                Page = 1,
                PageSize = SearchPageSize,
                FolderId = activeFolderId
            };
            var pagedResult = await BookmarkService.SearchBookmarksAsync(request, token);

            if (!token.IsCancellationRequested)
            {
                _highlightQuery = bookmarkQuery.Trim();
                _results = pagedResult.Items?.Select(MapBookmarkToItem).ToList() ?? [];
                AssignResultIndices();
                _selectedIndex = FirstSelectableIndex();
                RememberLoadedPage(request, pagedResult.TotalCount);
                _loadedBookmarkCount = _results.Count;
                StateHasChanged();
            }
        }
        catch (TaskCanceledException)
        {
        }
        catch
        {
            _results.Clear();
            StateHasChanged();
        }
    }

    private void AssignResultIndices()
    {
        for (var i = 0; i < _results.Count; i++)
            _results[i].Index = i;
    }

    private int FirstSelectableIndex()
    {
        for (var i = 0; i < _results.Count; i++)
        {
            if (!_results[i].IsSectionHeader)
                return i;
        }
        return 0;
    }

    private void RebuildFolderPathMap(List<FolderTreeNodeDto>? nodes)
    {
        _folderPathById = new Dictionary<Guid, string>();
        BuildFolderPathMapRecursive(nodes, string.Empty);
    }

    private void BuildFolderPathMapRecursive(List<FolderTreeNodeDto>? nodes, string currentPath)
    {
        if (nodes == null) return;
        foreach (var node in nodes)
        {
            var newPath = string.IsNullOrEmpty(currentPath) ? node.Title : $"{currentPath} / {node.Title}";
            _folderPathById[node.Id] = newPath;
            if (node.Children is { Count: > 0 })
            {
                BuildFolderPathMapRecursive(node.Children, newPath);
            }
        }
    }

    private void FindFoldersRecursive(List<FolderTreeNodeDto>? nodes, string query, string currentPath, List<FolderSearchResult> results)
    {
        if (nodes == null) return;
        foreach (var node in nodes)
        {
            var newPath = string.IsNullOrEmpty(currentPath) ? node.Title : $"{currentPath} / {node.Title}";
            if (node.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new FolderSearchResult
                {
                    Id = node.Id,
                    Title = node.Title,
                    Path = newPath
                });
            }
            if (node.Children != null)
            {
                FindFoldersRecursive(node.Children, query, newPath, results);
            }
        }
    }

    private Guid? FindFolderIdByName(List<FolderTreeNodeDto>? nodes, string name)
    {
        if (nodes == null) return null;
        foreach (var node in nodes)
        {
            if (node.Title.Equals(name, StringComparison.OrdinalIgnoreCase))
                return node.Id;
            
            if (node.Children != null)
            {
                var childId = FindFolderIdByName(node.Children, name);
                if (childId.HasValue)
                    return childId;
            }
        }
        return null;
    }

    private FolderTreeNodeDto? FindFolderById(List<FolderTreeNodeDto>? nodes, Guid id)
    {
        if (nodes == null) return null;
        foreach (var node in nodes)
        {
            if (node.Id == id)
                return node;
            
            if (node.Children != null)
            {
                var child = FindFolderById(node.Children, id);
                if (child != null)
                    return child;
            }
        }
        return null;
    }

    [JSInvokable]
    public void TogglePalette()
    {
        PaletteService.Toggle();
    }

    [JSInvokable]
    public void ClosePalette()
    {
        PaletteService.Close();
    }

    /// <summary>
    /// Invoked when the extension re-shows a kept-alive palette iframe. Reopens
    /// (or re-resets) the palette so it comes back fresh and focused.
    /// </summary>
    [JSInvokable]
    public void OpenPalette()
    {
        if (!PaletteService.IsOpen)
        {
            PaletteService.Open();
        }
        else
        {
            OnToggle();
        }
    }

    [JSInvokable]
    public async Task NavigateList(int direction)
    {
        // History recall: ArrowUp on empty input walks recent searches (C#-only state machine).
        if (_historyIndex is null
            && string.IsNullOrEmpty(_searchQuery)
            && direction == -1
            && _searchHistory.Count > 0)
        {
            _historyIndex = 0;
            await ApplyHistoryEntryAsync(_searchHistory[0]);
            return;
        }

        if (_historyIndex is int histIdx)
        {
            if (direction == -1)
            {
                var next = Math.Min(histIdx + 1, _searchHistory.Count - 1);
                if (next == histIdx)
                    return; // Already at oldest — avoid re-firing the same search.
                _historyIndex = next;
                await ApplyHistoryEntryAsync(_searchHistory[next]);
                return;
            }

            if (direction == 1)
            {
                // Exit history mode; keep the recalled query and current results.
                // Selection already sits on a result row — do not advance on this keypress.
                _historyIndex = null;
                StateHasChanged();
                _ = JSRuntime.InvokeVoidAsync("scrollPaletteToIndex", _selectedIndex, ItemSizePx);
                return;
            }
        }

        if (_results.Count == 0) return;
        if (_results.All(r => r.IsSectionHeader)) return;

        var start = _selectedIndex;
        do
        {
            _selectedIndex = (_selectedIndex + direction + _results.Count) % _results.Count;
        }
        while (_results[_selectedIndex].IsSectionHeader && _selectedIndex != start);

        if (_results[_selectedIndex].IsSectionHeader)
            return;

        StateHasChanged();
        _ = JSRuntime.InvokeVoidAsync("scrollPaletteToIndex", _selectedIndex, ItemSizePx);
    }

    private async Task ApplyHistoryEntryAsync(string query)
    {
        _searchQuery = query;
        await JSRuntime.InvokeVoidAsync("setPaletteInput", query);
        await RunSearchAsync(query, debounce: false);
        // Keep history mode active — RunSearchAsync must not clear _historyIndex.
        StateHasChanged();
    }

    [JSInvokable]
    public async Task ExecutePrimary()
    {
        if (_results.Count == 0 || _selectedIndex >= _results.Count) return;

        var selected = _results[_selectedIndex];
        if (selected.IsSectionHeader) return;
        if (selected.IsFolder)
        {
            await AutocompleteFolder(selected);
            return;
        }

        _ = RecordFrecencyOpenAsync(selected);
        _ = RecordSearchHistoryIfNeededAsync();

        if (Embedded)
        {
            // On a manager tab Enter deep-links inside that dashboard; on any
            // other site it navigates the tab to the bookmark itself.
            var url = IsContextOnManager ? BuildManagerDeepLink(selected, GetContextOrigin()) : selected.Url;
            await NavigateCurrentTabAsync(url);
            return;
        }

        var relativeUri = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var currentRoute = "/" + relativeUri.Split('?')[0];

        if (currentRoute.StartsWith("/library", StringComparison.OrdinalIgnoreCase))
        {
            await OpenInNewTabAsync(selected);
        }
        else
        {
            GoToBookmark(selected);
        }
    }

    [JSInvokable]
    public async Task ExecuteSecondary()
    {
        if (_results.Count == 0 || _selectedIndex >= _results.Count) return;

        var selected = _results[_selectedIndex];
        if (selected.IsSectionHeader) return;
        if (selected.IsFolder)
        {
            await AutocompleteFolder(selected);
            return;
        }

        _ = RecordFrecencyOpenAsync(selected);
        _ = RecordSearchHistoryIfNeededAsync();

        if (Embedded)
        {
            await OpenTabViaExtensionAsync(selected.Url);
            return;
        }

        var relativeUri = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var currentRoute = "/" + relativeUri.Split('?')[0];

        if (currentRoute.StartsWith("/library", StringComparison.OrdinalIgnoreCase))
        {
            GoToBookmark(selected);
        }
        else
        {
            await OpenInNewTabAsync(selected);
        }
    }

    /// <summary>
    /// Ctrl+Enter. Embedded: opens the bookmark in the manager dashboard (new
    /// tab, deep link with highlight). Native: same as Go to Bookmark.
    /// </summary>
    [JSInvokable]
    public async Task ExecuteTertiary()
    {
        if (_results.Count == 0 || _selectedIndex >= _results.Count) return;

        var selected = _results[_selectedIndex];
        if (selected.IsSectionHeader) return;
        if (selected.IsFolder)
        {
            await AutocompleteFolder(selected);
            return;
        }

        _ = RecordFrecencyOpenAsync(selected);
        _ = RecordSearchHistoryIfNeededAsync();

        if (Embedded)
        {
            var managerOrigin = NavigationManager.BaseUri.TrimEnd('/');
            await OpenTabViaExtensionAsync(BuildManagerDeepLink(selected, managerOrigin));
            return;
        }

        GoToBookmark(selected);
    }

    private Task RecordFrecencyOpenAsync(PaletteItem item) =>
        FrecencyService.RecordOpenAsync(item.Id, item.Title, item.Url, item.Category);

    private async Task RecordSearchHistoryIfNeededAsync()
    {
        if (string.IsNullOrEmpty(_loadedQuery) || string.IsNullOrWhiteSpace(_searchQuery))
            return;
        await SearchHistoryService.RecordAsync(_searchQuery);
        await RefreshSearchHistoryAsync();
    }

    private async Task AutocompleteFolder(PaletteItem selected)
    {
        _filterFolderId = selected.Id;
        _searchQuery = $">{selected.Title} ";
        _selectedIndex = 0;
        _results.Clear();
        StateHasChanged();

        _ = JSRuntime.InvokeVoidAsync("setPaletteInput", _searchQuery);

        await SearchFolderBookmarksAsync(_filterFolderId.Value);
    }

    private async Task SearchFolderBookmarksAsync(Guid folderId)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            var request = new SearchRequest
            {
                Query = string.Empty,
                Page = 1,
                PageSize = SearchPageSize,
                FolderId = folderId
            };
            var pagedResult = await BookmarkService.SearchBookmarksAsync(request, token);
            
            if (!token.IsCancellationRequested)
            {
                _highlightQuery = string.Empty;
                _results = pagedResult.Items?.Select(MapBookmarkToItem).ToList() ?? [];
                AssignResultIndices();
                _selectedIndex = FirstSelectableIndex();
                RememberLoadedPage(request, pagedResult.TotalCount);
                _loadedBookmarkCount = _results.Count;
                StateHasChanged();
            }
        }
        catch (TaskCanceledException)
        {
        }
        catch
        {
            _results.Clear();
            StateHasChanged();
        }
    }

    private async Task HandleItemClick(int index)
    {
        if (index < 0 || index >= _results.Count || _results[index].IsSectionHeader)
            return;
        _selectedIndex = index;
        await ExecutePrimary();
    }

    private async Task OpenInNewTabAsync(PaletteItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Url))
        {
            await JSRuntime.InvokeVoidAsync("openInNewTab", item.Url);
            PaletteService.Close();
        }
    }

    private void GoToBookmark(PaletteItem item)
    {
        PaletteService.Close();
        NavigationManager.NavigateTo($"/bookmarks?bookmarkId={item.Id}");
    }

    // ── Embedded (in-tab iframe) helpers ─────────────────────────────────────

    private bool IsContextOnManager =>
        Uri.TryCreate(ContextUrl, UriKind.Absolute, out var context)
        && Uri.TryCreate(NavigationManager.BaseUri, UriKind.Absolute, out var baseUri)
        && string.Equals(context.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Origin of the overlaid tab (scheme + host + port), used so a deep link
    /// opened on a manager tab stays on the origin the user is browsing.
    /// </summary>
    private string GetContextOrigin()
    {
        return Uri.TryCreate(ContextUrl, UriKind.Absolute, out var context)
            ? context.GetLeftPart(UriPartial.Authority)
            : NavigationManager.BaseUri.TrimEnd('/');
    }

    private static string BuildManagerDeepLink(PaletteItem item, string origin) =>
        $"{origin}/bookmarks?bookmarkId={item.Id}";

    private async Task NavigateCurrentTabAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        PaletteService.Close();
        await JSRuntime.InvokeVoidAsync("paletteEmbedded.navigate", url);
    }

    private async Task OpenTabViaExtensionAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        PaletteService.Close();
        await JSRuntime.InvokeVoidAsync("paletteEmbedded.openNewTab", url);
    }

    private string GetBadgeClass(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return string.Empty;
        return category.ToLowerInvariant() switch
        {
            "anime" => "badge-anime",
            "manga" => "badge-manga",
            "novel" => "badge-novel",
            "folder" => "badge-folder",
            _ => string.Empty
        };
    }

    private PaletteItem MapBookmarkToItem(BookmarkNodeDto bookmark)
    {
        string? folderPath = null;
        if (bookmark.ParentId is { } parentId
            && _folderPathById.TryGetValue(parentId, out var path))
        {
            folderPath = path;
        }

        var tags = bookmark.Metadata?.Tags;
        var tooltip = BuildItemTooltip(bookmark.Title, tags);

        return new PaletteItem
        {
            Id = bookmark.Id,
            Title = bookmark.Title,
            TitleHtml = PaletteTitleHighlighter.BuildTitleHtml(bookmark.Title, _highlightQuery),
            Subtitle = FormatBookmarkSubtitle(folderPath, bookmark.Url),
            Category = bookmark.Metadata?.Category,
            Url = bookmark.Url,
            IsFolder = false,
            Tooltip = tooltip,
            BookmarkNode = bookmark
        };
    }

    private PaletteItem MapFolderToItem(FolderSearchResult folder)
    {
        return new PaletteItem
        {
            Id = folder.Id,
            Title = folder.Title,
            TitleHtml = PaletteTitleHighlighter.BuildTitleHtml(folder.Title, string.Empty),
            Subtitle = folder.Path,
            Category = "Folder",
            IsFolder = true,
            Tooltip = folder.Title
        };
    }

    private static string BuildItemTooltip(string title, IReadOnlyList<string>? tags)
    {
        if (tags is { Count: > 0 })
            return $"{title}\nTags: {string.Join(", ", tags)}";
        return title;
    }

    /// <summary>
    /// Location-first subtitle: folder breadcrumb and/or URL host.
    /// Extracted for unit testing.
    /// </summary>
    public static string FormatBookmarkSubtitle(string? folderPath, string? url)
    {
        string? host = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                host = new Uri(url).Host;
                if (string.IsNullOrEmpty(host))
                    host = null;
            }
            catch
            {
                host = null;
            }
        }

        var hasPath = !string.IsNullOrWhiteSpace(folderPath);
        var hasHost = !string.IsNullOrWhiteSpace(host);

        if (hasPath && hasHost)
            return $"{folderPath} · {host}";
        if (hasPath)
            return folderPath!;
        if (hasHost)
            return host!;
        return "Bookmark";
    }

    private string? GetFaviconUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            var host = new Uri(url).Host;
            return string.IsNullOrEmpty(host)
                ? null
                : $"https://www.google.com/s2/favicons?domain={host}&sz=16";
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        PaletteService.OnToggle -= OnToggle;
        NavigationManager.LocationChanged -= OnLocationChanged;
        _ctrlPRegistration?.Dispose();
        _dotNetRef?.Dispose();
        _searchCts?.Dispose();
    }
}
