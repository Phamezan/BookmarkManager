using BookmarkManager.Client.Features.Library;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Library : IDisposable
{
    private const string SortNewTrending = "trending";
    private const string SortRating = "rating";
    private const string SortUpdated = "updated";
    private const string SortAlpha = "alpha";
    private const int SearchDebounceMs = 400;
    private const int MinQueryLength = 2;
    private const int CollapsedGenreGroupLimit = 3;

    private static readonly (string Key, string Label)[] SortOptions =
    {
        (SortNewTrending, "New & trending"),
        (SortRating, "Top rated"),
        (SortUpdated, "Recently updated"),
        (SortAlpha, "A – Z")
    };

    private enum HeroSlide
    {
        Trending,
        Recommends
    }

    private static string SortLabel(string sort) =>
        SortOptions.FirstOrDefault(option => option.Key == sort).Label ?? "New & trending";

    private static readonly LibraryMediaType[] MediaTypes =
    {
        LibraryMediaType.Manga,
        LibraryMediaType.Manhwa,
        LibraryMediaType.LightNovel,
        LibraryMediaType.Webnovel
    };

    private const int TrendingPageSize = 48;
    private const int HeroRailSize = 12;
    private const int RecommendsPoolSize = 96;
    private const int RecommendsGridSize = 18;

    private List<LibraryItem> _hero = new();
    private List<LibraryItem> _recommendsPool = new();
    private List<LibraryItem> _recommendsGrid = new();
    private List<LibraryItem> _trending = new();
    private List<LibraryItem> _searchResults = new();
    private int _trendingTotalCount;
    private bool _trendingHasMore;
    private bool _loadingMore;

    private string _search = string.Empty;
    private LibraryMediaType? _selectedType;
    private LibraryMediaType? _recommendsType;
    private readonly HashSet<string> _selectedGenres = new(StringComparer.OrdinalIgnoreCase);
    private string _sort = SortNewTrending;
    private bool _myBookmarksOnly;
    private HeroSlide _heroSlide = HeroSlide.Trending;
    private int _featuredIndex;
    private int _recommendsSeed = 1;
    private bool _genresExpanded;
    private bool _loading;
    private bool _loadingRecommends;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _recommendsCts;
    private LibraryBookmarkExclusions _bookmarkExclusions = LibraryBookmarkExclusions.Empty;
    private Dictionary<string, LibraryReadingProgressDto> _readingProgress = new();
    private List<LibraryItem> _myBookmarksItems = new();

    protected override async Task OnInitializedAsync()
    {
        await Task.WhenAll(
            LoadTrendingAsync(),
            LoadRecommendsPoolAsync(),
            LoadBookmarkExclusionsAsync(),
            LoadReadingProgressAndMatchedSeriesAsync());
    }

    private LibraryReadingProgressDto? ProgressFor(LibraryItem item)
    {
        if (!_readingProgress.TryGetValue(LibraryReadingProgressKey.Build(item.Provider, item.ProviderId), out var progress))
            return null;

        if (progress.LatestChapterNumber is not null)
            return progress;

        var latestFromCatalog = LibraryLatestChapterParser.Parse(item.LatestChapter);
        return latestFromCatalog is null
            ? progress
            : progress with { LatestChapterNumber = latestFromCatalog };
    }

    private string? ProgressBadgeTextFor(LibraryItem item) =>
        LibraryProgressDisplay.BadgeText(ProgressFor(item));

    private void GoToBookmark(LibraryItem item)
    {
        if (ProgressFor(item)?.BookmarkId is { } bookmarkId)
            NavigationManager.NavigateTo($"/bookmarks?bookmarkId={bookmarkId}");
    }

    // Both loads key off the same server-side bookmark/series match set, so they're fetched
    // together: the progress dict badges whatever cards happen to already be on screen, while
    // the matched-series list is the "My bookmarks" filter's own item source - those matched
    // series are frequently NOT among the currently loaded trending/search page, so the filter
    // can't just narrow ActiveItems.
    private async Task LoadReadingProgressAndMatchedSeriesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var progressTask = LibraryService.GetReadingProgressAsync(cancellationToken);
            var matchedSeriesTask = LibraryService.GetMyBookmarkedSeriesAsync(cancellationToken);
            await Task.WhenAll(progressTask, matchedSeriesTask);
            if (cancellationToken.IsCancellationRequested)
                return;

            _readingProgress = progressTask.Result.ToDictionary(p => LibraryReadingProgressKey.Build(p.Provider, p.ProviderId));
            _myBookmarksItems = matchedSeriesTask.Result.Select(dto => LibraryItem.FromDto(dto)).ToList();
            StateHasChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _readingProgress = new Dictionary<string, LibraryReadingProgressDto>();
            _myBookmarksItems = new List<LibraryItem>();
        }
    }

    private bool IsSearchActive => _search.Trim().Length >= MinQueryLength;

    private IReadOnlyList<LibraryItem> ActiveItems => IsSearchActive ? _searchResults : _trending;

    private LibraryItem? Featured =>
        _hero.Count == 0 ? null : _hero[Math.Clamp(_featuredIndex, 0, _hero.Count - 1)];

    private void SelectFeatured(int index) => _featuredIndex = index;

    private void SetHeroSlide(HeroSlide slide)
    {
        _heroSlide = slide;
        _featuredIndex = 0;
        if (slide == HeroSlide.Recommends && _recommendsGrid.Count == 0 && !_loadingRecommends)
            _ = LoadRecommendsPoolAsync();
    }

    private void ShuffleRecommends()
    {
        _recommendsSeed++;
        RebuildRecommendsGrid();
        _heroSlide = HeroSlide.Recommends;
    }

    private Task SelectRecommendsType(LibraryMediaType? type)
    {
        if (_recommendsType == type)
            return Task.CompletedTask;

        _recommendsType = type;
        return LoadRecommendsPoolAsync();
    }

    private string RecommendsTabClass(LibraryMediaType? type) =>
        _recommendsType == type ? "lib-recommends-tab is-active" : "lib-recommends-tab";

    private void RebuildRecommendsGrid() =>
        _recommendsGrid = LibraryRecommends.BuildRail(
            _recommendsPool,
            RecommendsGridSize,
            _recommendsSeed,
            _bookmarkExclusions);

    private void ToggleGenresExpanded() => _genresExpanded = !_genresExpanded;

    private bool HasActiveFilters =>
        IsSearchActive || _selectedType is not null || _selectedGenres.Count > 0 || _myBookmarksOnly;

    private IReadOnlyList<string> AllGenres =>
        ActiveItems.SelectMany(item => item.Genres)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(genre => genre, StringComparer.OrdinalIgnoreCase)
                .ToList();

    private IReadOnlyList<LibraryGenreTaxonomy.GenreGroup> GenreGroups =>
        LibraryGenreTaxonomy.GroupGenres(AllGenres);

    private IReadOnlyList<LibraryGenreTaxonomy.GenreGroup> VisibleGenreGroups
    {
        get
        {
            if (_genresExpanded || GenreGroups.Count <= CollapsedGenreGroupLimit)
                return GenreGroups;

            var selected = _selectedGenres.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var withSelection = GenreGroups
                .Where(group => group.Tags.Any(tag => selected.Contains(tag)))
                .ToList();

            var remainder = GenreGroups
                .Where(group => withSelection.All(selectedGroup => !string.Equals(selectedGroup.Label, group.Label, StringComparison.OrdinalIgnoreCase)))
                .Take(Math.Max(0, CollapsedGenreGroupLimit - withSelection.Count));

            return withSelection.Concat(remainder).Take(CollapsedGenreGroupLimit).ToList();
        }
    }

    private bool ShowGenreExpandToggle => GenreGroups.Count > CollapsedGenreGroupLimit;

    private IReadOnlyList<LibraryItem> TrendingItems => _hero;

    private bool CanLoadMore => !IsSearchActive && !_myBookmarksOnly && _trendingHasMore;

    private IReadOnlyList<LibraryItem> FilteredItems =>
        _myBookmarksOnly
            ? SortByCatchUpGap(FilterItems()).ToList()
            : SortItems(FilterItems()).ToList();

    private IEnumerable<LibraryItem> FilterItems()
    {
        // The "My bookmarks" filter has its own item source (server-matched series, independent
        // of pagination) rather than narrowing whatever's currently loaded in ActiveItems -
        // matched series are frequently not on the currently loaded trending/search page at all.
        var query = (_myBookmarksOnly ? _myBookmarksItems : ActiveItems).AsEnumerable();

        if (_selectedGenres.Count > 0)
        {
            query = query.Where(item => item.Genres.Any(genre => _selectedGenres.Contains(genre)));
        }

        return query;
    }

    private IEnumerable<LibraryItem> SortByCatchUpGap(IEnumerable<LibraryItem> items) =>
        items.OrderByDescending(item => CatchUpGap(item) ?? double.NegativeInfinity);

    private double? CatchUpGap(LibraryItem item)
    {
        var progress = ProgressFor(item);
        return progress is { CurrentChapter: { } current, LatestChapterNumber: { } latest }
            ? latest - current
            : null;
    }

    private void ToggleMyBookmarksOnly() => _myBookmarksOnly = !_myBookmarksOnly;

    private IEnumerable<LibraryItem> SortItems(IEnumerable<LibraryItem> items) =>
        _sort switch
        {
            SortRating => items.OrderByDescending(item => item.Rating ?? -1),
            SortUpdated => items.OrderByDescending(item => item.LastReleaseAt ?? DateTimeOffset.MinValue),
            SortAlpha => items.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderByDescending(item => item.LastReleaseAt ?? DateTimeOffset.MinValue)
                      .ThenByDescending(item => item.Rating ?? -1)
                      .ThenByDescending(item => item.IsTrending)
        };

    private static string TypeLabel(LibraryMediaType type) =>
        type == LibraryMediaType.LightNovel ? "Light Novel" : type.ToString();

    private string TabClass(LibraryMediaType? type) =>
        _selectedType == type ? "lib-tab is-active" : "lib-tab";

    private Task SelectType(LibraryMediaType? type)
    {
        _selectedType = type;
        return ReloadAsync();
    }

    private void ToggleGenre(string genre)
    {
        if (!_selectedGenres.Remove(genre))
        {
            _selectedGenres.Add(genre);
        }
    }

    private Task ClearFilters()
    {
        _search = string.Empty;
        _selectedType = null;
        _selectedGenres.Clear();
        _myBookmarksOnly = false;
        return ReloadAsync();
    }

    private void OnSearchInput(ChangeEventArgs args)
    {
        _search = args.Value?.ToString() ?? string.Empty;
        _ = DebouncedSearchAsync();
    }

    private Task ClearSearch()
    {
        _search = string.Empty;
        return ReloadAsync();
    }

    private async Task DebouncedSearchAsync()
    {
        var cts = ResetSearchCts();

        try
        {
            await Task.Delay(SearchDebounceMs, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Superseded by a newer keystroke - that flow owns the state now.
            return;
        }

        if (cts.IsCancellationRequested)
            return;

        await RunSearchAsync(cts.Token);
    }

    private Task ReloadAsync() => RunSearchAsync(ResetSearchCts().Token);

    private CancellationTokenSource ResetSearchCts()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        return cts;
    }

    private async Task RunSearchAsync(CancellationToken cancellationToken)
    {
        if (!IsSearchActive)
        {
            await LoadTrendingAsync(cancellationToken);
            return;
        }

        _loading = true;
        StateHasChanged();

        try
        {
            var response = await LibraryService.SearchAsync(_search.Trim(), _selectedType, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            _searchResults = response.Items.Select(dto => LibraryItem.FromDto(dto)).ToList();
            WarnOnProviderFailures(response);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search - the newer call owns updating state.
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                Snackbar.Add($"Library search failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private async Task LoadTrendingAsync(CancellationToken cancellationToken = default)
    {
        _loading = true;
        StateHasChanged();

        try
        {
            var response = await LibraryService.GetTrendingAsync(_selectedType, skip: 0, take: TrendingPageSize, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            var items = response.Items.Select(dto => LibraryItem.FromDto(dto, isTrending: true)).ToList();
            _trending = items;
            _hero = items.Take(HeroRailSize).ToList();
            _trendingTotalCount = response.TotalCount;
            _trendingHasMore = response.HasMore;
            _featuredIndex = 0;
            WarnOnProviderFailures(response);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                Snackbar.Add($"Failed to load trending titles: {ex.Message}", Severity.Error);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _loading = false;
                StateHasChanged();
            }
        }
    }

    private async Task LoadBookmarkExclusionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var signals = new List<BookmarkSignal>();
            var page = 1;
            const int pageSize = 100;

            while (true)
            {
                var result = await BookmarkService.SearchBookmarksAsync(new SearchRequest
                {
                    Query = string.Empty,
                    Page = page,
                    PageSize = pageSize
                }, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                    return;

                foreach (var bookmark in result.Items)
                {
                    if (!string.IsNullOrWhiteSpace(bookmark.Url) || bookmark.AniListId is > 0)
                        signals.Add(new BookmarkSignal(bookmark.Url, bookmark.Title, bookmark.AniListId));
                }

                if (page * pageSize >= result.TotalCount || result.Items.Count == 0)
                    break;

                page++;
            }

            _bookmarkExclusions = LibraryBookmarkExclusions.FromBookmarks(signals);
            if (_recommendsPool.Count > 0)
                RebuildRecommendsGrid();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _bookmarkExclusions = LibraryBookmarkExclusions.Empty;
        }
    }

    private async Task LoadRecommendsPoolAsync(CancellationToken cancellationToken = default)
    {
        _recommendsCts?.Cancel();
        _recommendsCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _recommendsCts = cts;

        _loadingRecommends = true;
        StateHasChanged();

        try
        {
            var response = await LibraryService.GetTrendingAsync(_recommendsType, skip: 0, take: RecommendsPoolSize, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            _recommendsPool = response.Items.Select(dto => LibraryItem.FromDto(dto, isTrending: true)).ToList();
            RebuildRecommendsGrid();
            WarnOnProviderFailures(response);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                Snackbar.Add($"Failed to load recommends: {ex.Message}", Severity.Error);
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                _loadingRecommends = false;
                StateHasChanged();
            }
        }
    }

    private async Task LoadMoreAsync()
    {
        if (_loadingMore || !CanLoadMore)
            return;

        // Join the search CTS so a search/filter change started mid-flight cancels this append
        // instead of letting stale trending rows land under the new UI state.
        var cancellationToken = (_searchCts ??= new CancellationTokenSource()).Token;
        _loadingMore = true;
        StateHasChanged();

        try
        {
            var response = await LibraryService.GetTrendingAsync(_selectedType, skip: _trending.Count, take: TrendingPageSize, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            var items = response.Items.Select(dto => LibraryItem.FromDto(dto, isTrending: true)).ToList();
            _trending = _trending.Concat(items).ToList();
            _trendingTotalCount = response.TotalCount;
            _trendingHasMore = response.HasMore;
            WarnOnProviderFailures(response);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a new search/filter - that flow owns the state now.
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
                Snackbar.Add($"Failed to load more titles: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loadingMore = false;
            StateHasChanged();
        }
    }

    private void WarnOnProviderFailures(LibrarySearchResponse response)
    {
        var problems = response.ProviderStatuses
            .Where(s => s.Status is LibraryProviderResultStatus.Failed or LibraryProviderResultStatus.Timeout)
            .Select(s => s.Provider)
            .ToList();

        if (problems.Count > 0)
        {
            Snackbar.Add($"Some providers didn't respond ({string.Join(", ", problems)}) — showing partial results.", Severity.Warning);
        }
    }

    private async Task ShowDetailsAsync(LibraryItem item)
    {
        var displayItem = item;
        if (NeedsEnrichment(item))
        {
            try
            {
                var enriched = await LibraryService.EnrichCatalogEntryAsync(item.Provider, item.ProviderId);
                if (enriched is not null)
                {
                    displayItem = LibraryItem.FromDto(enriched, item.IsTrending);
                    ApplyEnrichedItem(displayItem);
                }
            }
            catch
            {
                // Popup still opens with whatever thin catalog data we already have.
            }
        }

        var parameters = new DialogParameters<BookmarkManager.Client.Features.Library.Components.MediaDetailsDialog>
        {
            { x => x.Item, displayItem },
            { x => x.Progress, ProgressFor(item) }
        };

        var options = new DialogOptions
        {
            CloseButton = false,
            MaxWidth = MaxWidth.Medium,
            FullWidth = true,
            NoHeader = true,
            BackdropClick = true,
            CloseOnEscapeKey = true
        };
        await DialogService.ShowAsync<BookmarkManager.Client.Features.Library.Components.MediaDetailsDialog>(string.Empty, parameters, options);
    }

    private static bool NeedsEnrichment(LibraryItem item) =>
        string.IsNullOrWhiteSpace(item.Synopsis) ||
        string.IsNullOrWhiteSpace(item.LatestChapter) ||
        item.Genres.Count == 0;

    private void ApplyEnrichedItem(LibraryItem enriched)
    {
        ReplaceInList(_trending, enriched);
        ReplaceInList(_hero, enriched);
        ReplaceInList(_recommendsPool, enriched);
        ReplaceInList(_recommendsGrid, enriched);
        ReplaceInList(_searchResults, enriched);
        StateHasChanged();
    }

    private static void ReplaceInList(List<LibraryItem> list, LibraryItem enriched)
    {
        var index = list.FindIndex(x =>
            string.Equals(x.Provider, enriched.Provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ProviderId, enriched.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            list[index] = enriched;
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _recommendsCts?.Cancel();
        _recommendsCts?.Dispose();
        _recommendsCts = null;
    }
}
