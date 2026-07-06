using System.Globalization;
using System.Text.Json;
using BookmarkManager.Client.Components;
using BookmarkManager.Client.Features.AnimeCalendar;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class AnimeCalendar
{
    private const string StorageKey = "animeCalendar.folderIds";

    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private Microsoft.JSInterop.IJSRuntime JSRuntime { get; set; } = default!;

    private bool _loading = true;
    private List<(Guid Id, string Title, int Depth)> _flatFolders = [];
    private HashSet<Guid> _selectedFolderIds = [];
    private List<AnimeCalendarItem> _items = [];
    private List<BookmarkNodeDto> _unmatchedBookmarks = [];
    private int _airingCount;
    private int _finishedCount;
    private AnimeCalendarView _view = AnimeCalendarView.Week;
    private DateTime _anchor = DateTime.Today;
    private bool _autoMatching;

    protected override async Task OnInitializedAsync()
    {
        var tree = await BookmarkService.GetFolderTreeAsync();
        _flatFolders = FlattenFolders(tree, 0);

        _selectedFolderIds = await LoadPersistedFolderIdsAsync();

        if (_selectedFolderIds.Count > 0)
        {
            await LoadScheduleAsync();
        }
        else
        {
            _loading = false;
        }
    }

    private static List<(Guid Id, string Title, int Depth)> FlattenFolders(List<FolderTreeNodeDto> nodes, int depth)
    {
        var result = new List<(Guid, string, int)>();
        foreach (var node in nodes)
        {
            result.Add((node.Id, node.Title, depth));
            result.AddRange(FlattenFolders(node.Children, depth + 1));
        }
        return result;
    }

    private async Task<HashSet<Guid>> LoadPersistedFolderIdsAsync()
    {
        try
        {
            var json = await JSRuntime.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrEmpty(json)) return [];
            return new HashSet<Guid>(JsonSerializer.Deserialize<List<Guid>>(json) ?? []);
        }
        catch
        {
            return [];
        }
    }

    private async Task PersistFolderIdsAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_selectedFolderIds);
            await JSRuntime.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
        }
        catch
        {
            // best-effort persistence only
        }
    }

    private async Task ToggleFolderAsync(Guid id, bool isSelected)
    {
        if (isSelected) _selectedFolderIds.Add(id);
        else _selectedFolderIds.Remove(id);

        await PersistFolderIdsAsync();
        await LoadScheduleAsync();
    }

    private async Task ClearFoldersAsync()
    {
        if (_selectedFolderIds.Count == 0) return;
        _selectedFolderIds.Clear();
        await PersistFolderIdsAsync();
        await LoadScheduleAsync();
    }

    private async Task LoadScheduleAsync()
    {
        _loading = true;
        StateHasChanged();
        try
        {
            var response = await BookmarkService.GetAnimeScheduleAsync(_selectedFolderIds.ToList());
            _items = response.Entries.Select(AnimeCalendarItem.FromEntry).ToList();
            _unmatchedBookmarks = response.UnmatchedBookmarks;
            _airingCount = response.AiringCount;
            _finishedCount = response.FinishedCount;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load anime calendar: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task AutoMatchAllAsync()
    {
        _autoMatching = true;
        try
        {
            var response = await BookmarkService.AutoMatchAnimeAsync(_selectedFolderIds.ToList());
            await LogAutoMatchResultAsync(response);
            if (response.AniListUnavailable)
            {
                Snackbar.Add("AniList's API is currently unavailable - try auto-matching again later.", Severity.Warning);
            }
            else
            {
                var attempted = response.Matched.Count + response.Skipped.Count;
                var cooldownNote = response.SkippedCooldownCount > 0
                    ? $" ({response.SkippedCooldownCount} already checked recently, skipped)"
                    : "";
                Snackbar.Add($"Matched {response.Matched.Count} of {attempted} anime checked{cooldownNote}.", Severity.Success);
            }
            await LoadScheduleAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Auto-match failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            _autoMatching = false;
        }
    }

    private async Task LogAutoMatchResultAsync(AutoMatchAnimeResponse response)
    {
        var matchedRows = response.Matched.Select(m => new
        {
            m.Title,
            m.SearchTitle,
            m.Source,
            Id = m.AniListId?.ToString(),
            m.MatchedTitle,
            m.Status
        }).ToList();
        var skippedRows = response.Skipped.Select(s => new { s.Title, s.SearchTitle, s.SkipReason }).ToList();

        await JSRuntime.InvokeVoidAsync("console.log", "[anime-calendar] auto-match matched:");
        await JSRuntime.InvokeVoidAsync("console.table", matchedRows);
        await JSRuntime.InvokeVoidAsync("console.log", "[anime-calendar] auto-match skipped:");
        await JSRuntime.InvokeVoidAsync("console.table", skippedRows);
    }

    private void OnItemClicked(AnimeCalendarItem item)
    {
        if (!string.IsNullOrEmpty(item.Url))
        {
            _ = JSRuntime.InvokeVoidAsync("open", item.Url, "_blank");
        }
    }

    private void SetView(AnimeCalendarView view) => _view = view;

    private void GoToday() => _anchor = DateTime.Today;

    // Prev/Next shift by a whole month for the Month view, otherwise by a week -
    // Agenda/Week span a week and Day steps a day at a time.
    private void GoPrev() => Shift(-1);
    private void GoNext() => Shift(1);

    private void Shift(int direction)
    {
        _anchor = _view switch
        {
            AnimeCalendarView.Month => _anchor.AddMonths(direction),
            AnimeCalendarView.Day => _anchor.AddDays(direction),
            _ => _anchor.AddDays(7 * direction)
        };
    }

    private string ToolbarEyebrow => _view switch
    {
        AnimeCalendarView.Day => "Today",
        AnimeCalendarView.Week => "Upcoming Episodes",
        _ => "Calendar"
    };

    private string ToolbarTitle => _view switch
    {
        AnimeCalendarView.Month => _anchor.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
        AnimeCalendarView.Day => _anchor.ToString("dddd, MMMM d", CultureInfo.CurrentCulture),
        _ => FormatWeekRange(_anchor)
    };

    // "July 6 – 12" style range for the week that contains the anchor (Sunday start).
    private static string FormatWeekRange(DateTime anchor)
    {
        var start = anchor.AddDays(-(int)anchor.DayOfWeek);
        var end = start.AddDays(6);
        return start.Month == end.Month
            ? $"{start:MMMM d} – {end:d}"
            : $"{start:MMM d} – {end:MMM d}";
    }
}
