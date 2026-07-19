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
        public bool IsTag { get; set; }
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
    private List<string> _filterTagNames = [];
    private List<TagCountDto> _allTags = [];
    private IDisposable? _ctrlPRegistration;

    // Load-more paging state: tracks the request shape of the currently-loaded page so
    // "Load more" can fetch the next page with the same query/folder/tag filter and append.
    private int _loadedPage = 1;
    private int _loadedPageSize = DefaultPageSize;
    private int _totalResultCount;
    private string _loadedQuery = string.Empty;
    private Guid? _loadedFolderId;
    private List<string> _loadedTags = [];
    private bool _isLoadingMore;
    /// <summary>Effective bookmark query used for title highlighting (empty for default results / folder autocomplete).</summary>
    private string _highlightQuery = string.Empty;
    /// <summary>Bookmark rows loaded for the current page sequence (excludes section headers / recent section).</summary>
    private int _loadedBookmarkCount;
    private int? _historyIndex;
    private IReadOnlyList<string> _searchHistory = [];
    private bool _hasSearchHistory;
    private string _listHeaderTitle = "Folders";

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

        if (PaletteService.IsOpen && _dotNetRef is not null)
        {
            // Re-bind if the list element was recreated after open/search.
            await JSRuntime.InvokeVoidAsync("ensurePaletteInfiniteScroll", _dotNetRef);
        }
    }

    private void OnToggle()
    {
        if (PaletteService.IsOpen)
        {
            _searchQuery = string.Empty;
            _selectedIndex = 0;
            _filterFolderId = null;
            _filterTagNames = [];
            _results.Clear();
            _totalResultCount = 0;
            _triggerStagger = true;
            _historyIndex = null;
            UpdateHints();
            _ = RefreshSearchHistoryAsync();
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            _ = LoadDefaultResultsAsync(_searchCts.Token);
            _ = LoadTagsAsync(_searchCts.Token);
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

            // Empty open = folder browser (same list as typing ">" with no filter).
            var folderMatches = new List<FolderSearchResult>();
            FindFoldersRecursive(folderTree, query: string.Empty, string.Empty, folderMatches);

            _highlightQuery = string.Empty;
            _filterFolderId = null;
            _results = folderMatches.Select(MapFolderToItem).ToList();
            AssignResultIndices();
            _selectedIndex = FirstSelectableIndex();
            _totalResultCount = _results.Count;
            _loadedBookmarkCount = _results.Count;
            _loadedPage = 1;
            _loadedPageSize = DefaultPageSize;
            _loadedQuery = string.Empty;
            _loadedFolderId = null;
            _listHeaderTitle = "Folders";
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

    /// <summary>Records the request shape + total count for a just-loaded first page so "Load more" can continue it.</summary>
    private void RememberLoadedPage(SearchRequest request, int totalCount)
    {
        _loadedPage = request.Page;
        _loadedPageSize = request.PageSize;
        _loadedQuery = request.Query;
        _loadedFolderId = request.FolderId;
        _loadedTags = request.Tags;
        _totalResultCount = totalCount;
    }

    /// <summary>
    /// Fetches the next page (same query/folder/tag filter as the currently-loaded results) and
    /// appends it. Triggered by the Load more button and by scrolling near the list bottom.
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
                FolderId = _loadedFolderId,
                Tags = _loadedTags
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
            // Fail silently — scroll/button can retry.
        }
        finally
        {
            _isLoadingMore = false;
            StateHasChanged();
        }
    }

    /// <summary>Called from command-palette.js when the list is scrolled near the bottom.</summary>
    [JSInvokable]
    public Task LoadMoreFromScroll() => LoadMoreResultsAsync();

    /// <summary>Loads the full tag+count list once per palette session for client-side "#" autocomplete.</summary>
    private async Task LoadTagsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _allTags = await BookmarkService.GetTagsAsync(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _allTags = [];
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
            _filterTagNames = [];
            await LoadDefaultResultsAsync(token);
            return;
        }

        var folderTree = await BookmarkService.GetFolderTreeAsync(token);
        RebuildFolderPathMap(folderTree);

        // Peel off leading ">Folder" / "#Tag" filter tokens, in any order and any combination
        // (e.g. ">Novels #action", "#action #comedy >Novels query"). Each resolved token narrows
        // the search; whatever remains once no more tokens match is the free-text bookmark query.
        // A trailing, still-being-typed token (no space after it yet) breaks out to show that
        // token's autocomplete suggestions instead of running a search.
        var remaining = _searchQuery;
        Guid? activeFolderId = null;
        var activeTags = new List<string>();

        while (remaining.StartsWith(">") || remaining.StartsWith("#"))
        {
            if (remaining.StartsWith(">"))
            {
                if (!remaining.Contains(' '))
                {
                    _filterFolderId = null;
                    ShowFolderAutocomplete(remaining.Substring(1).Trim(), folderTree);
                    return;
                }

                var (folderId, rest) = ResolveFolderToken(remaining, folderTree);
                if (folderId is null) break; // Unresolved — treat the rest (including ">") as free text.

                activeFolderId = folderId;
                _filterFolderId = folderId;
                remaining = rest;
            }
            else
            {
                if (!remaining.Contains(' '))
                {
                    _filterTagNames = activeTags;
                    ShowTagAutocomplete(remaining.Substring(1).Trim());
                    return;
                }

                var (tagName, rest) = ResolveTagToken(remaining);
                if (tagName is null) break;

                activeTags.Add(tagName);
                remaining = rest;
            }
        }

        if (activeFolderId is null) _filterFolderId = null;
        _filterTagNames = activeTags;
        var bookmarkQuery = remaining;

        try
        {
            if (debounce)
                await Task.Delay(200, token); // Debounce

            var request = new SearchRequest
            {
                Query = bookmarkQuery.Trim(),
                Page = 1,
                PageSize = SearchPageSize,
                FolderId = activeFolderId,
                Tags = activeTags
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
                _listHeaderTitle = "Bookmarks";
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

    /// <summary>
    /// Resolves a leading ">FolderName" token off <paramref name="remaining"/> (which contains
    /// a space). Prefers the already-locked <see cref="_filterFolderId"/> so folder names with
    /// spaces — picked via autocomplete — keep matching as the rest of the query is typed;
    /// otherwise falls back to an exact single-word name lookup.
    /// </summary>
    private (Guid? FolderId, string Remainder) ResolveFolderToken(string remaining, List<FolderTreeNodeDto>? folderTree)
    {
        if (_filterFolderId.HasValue)
        {
            var locked = FindFolderById(folderTree, _filterFolderId.Value);
            if (locked != null && remaining.StartsWith($">{locked.Title}", StringComparison.OrdinalIgnoreCase))
            {
                return (locked.Id, StripToken(remaining, locked.Title.Length + 1));
            }
        }

        var spaceIndex = remaining.IndexOf(' ');
        if (spaceIndex <= 1) return (null, remaining);

        var candidateName = remaining.Substring(1, spaceIndex - 1).Trim();
        var id = FindFolderIdByName(folderTree, candidateName);
        return id.HasValue ? (id, StripToken(remaining, candidateName.Length + 1)) : (null, remaining);
    }

    /// <summary>Same idea as <see cref="ResolveFolderToken"/> but for a leading "#TagName" token.</summary>
    private (string? Tag, string Remainder) ResolveTagToken(string remaining)
    {
        var locked = _filterTagNames.FirstOrDefault(t => remaining.StartsWith($"#{t}", StringComparison.OrdinalIgnoreCase));
        if (locked != null)
        {
            return (locked, StripToken(remaining, locked.Length + 1));
        }

        var spaceIndex = remaining.IndexOf(' ');
        if (spaceIndex <= 1) return (null, remaining);

        var candidateName = remaining.Substring(1, spaceIndex - 1).Trim();
        var match = _allTags.FirstOrDefault(t => t.Tag.Equals(candidateName, StringComparison.OrdinalIgnoreCase));
        return match != null ? (match.Tag, StripToken(remaining, candidateName.Length + 1)) : (null, remaining);
    }

    /// <summary>Drops a resolved "&gt;Name" / "#Name" token (and one following space, if any) off the front.</summary>
    private static string StripToken(string remaining, int tokenLength)
    {
        var prefixLength = tokenLength; // includes the leading '>' or '#'
        if (remaining.Length > prefixLength && remaining[prefixLength] == ' ')
            prefixLength++;
        return remaining.Substring(prefixLength);
    }

    private void ShowFolderAutocomplete(string folderQuery, List<FolderTreeNodeDto>? folderTree)
    {
        var folderMatches = new List<FolderSearchResult>();
        FindFoldersRecursive(folderTree, folderQuery, string.Empty, folderMatches);

        _results = folderMatches.Select(MapFolderToItem).ToList();
        // Folder autocomplete returns everything in one shot — no paging, so "Load more" never shows.
        _highlightQuery = string.Empty;
        AssignResultIndices();
        _totalResultCount = _results.Count;
        _loadedBookmarkCount = _results.Count;
        _listHeaderTitle = "Folders";
        StateHasChanged();
    }

    /// <summary>
    /// Tags are cached client-side (<see cref="_allTags"/>, loaded once per palette session) since a
    /// bookmark manager can easily carry hundreds of tags — filtering that list locally on every
    /// keystroke is instant and avoids a round trip per character typed.
    /// </summary>
    private void ShowTagAutocomplete(string tagQuery)
    {
        var tagMatches = string.IsNullOrEmpty(tagQuery)
            ? _allTags
            : _allTags.Where(t => t.Tag.Contains(tagQuery, StringComparison.OrdinalIgnoreCase)).ToList();

        _results = tagMatches.Select(MapTagToItem).ToList();
        _highlightQuery = string.Empty;
        AssignResultIndices();
        _selectedIndex = FirstSelectableIndex();
        _totalResultCount = _results.Count;
        _loadedBookmarkCount = _results.Count;
        _listHeaderTitle = "Tags";
        StateHasChanged();
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
        if (selected.IsTag)
        {
            await AutocompleteTag(selected);
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
        if (selected.IsTag)
        {
            await AutocompleteTag(selected);
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
        if (selected.IsTag)
        {
            await AutocompleteTag(selected);
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

    /// <summary>
    /// Completes the trailing (still-being-typed) ">Folder" token that produced the current
    /// autocomplete list, keeping any already-resolved filter tokens before it intact — so
    /// picking a folder after "#action " (or before typing a tag next) composes rather than
    /// replaces the query.
    /// </summary>
    private async Task AutocompleteFolder(PaletteItem selected)
    {
        _filterFolderId = selected.Id;
        _searchQuery = $"{GetPrefixBeforeLastToken(_searchQuery)}>{selected.Title} ";
        _selectedIndex = 0;
        _results.Clear();
        StateHasChanged();

        _ = JSRuntime.InvokeVoidAsync("setPaletteInput", _searchQuery);
        await RunSearchAsync(_searchQuery, debounce: false);
    }

    /// <summary>Same idea as <see cref="AutocompleteFolder"/> but for a trailing "#Tag" token.</summary>
    private async Task AutocompleteTag(PaletteItem selected)
    {
        _searchQuery = $"{GetPrefixBeforeLastToken(_searchQuery)}#{selected.Title} ";
        _selectedIndex = 0;
        _results.Clear();
        StateHasChanged();

        _ = JSRuntime.InvokeVoidAsync("setPaletteInput", _searchQuery);
        await RunSearchAsync(_searchQuery, debounce: false);
    }

    /// <summary>Everything up to and including the space before the query's last (incomplete) token.</summary>
    private static string GetPrefixBeforeLastToken(string query)
    {
        var lastSpace = query.LastIndexOf(' ');
        return lastSpace >= 0 ? query.Substring(0, lastSpace + 1) : string.Empty;
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

    private static PaletteItem MapTagToItem(TagCountDto tag)
    {
        return new PaletteItem
        {
            Id = Guid.Empty,
            Title = tag.Tag,
            TitleHtml = PaletteTitleHighlighter.BuildTitleHtml(tag.Tag, string.Empty),
            Subtitle = $"{tag.Count} bookmark{(tag.Count == 1 ? "" : "s")}",
            Category = "Tag",
            IsTag = true,
            Tooltip = tag.Tag
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
