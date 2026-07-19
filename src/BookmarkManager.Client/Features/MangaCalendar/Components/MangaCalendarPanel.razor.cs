using System.Globalization;
using BookmarkManager.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace BookmarkManager.Client.Features.MangaCalendar.Components;

/// <summary>Self-contained manhwa release calendar - pulls MangaDex's global chapter feed directly,
/// with no bookmark/folder matching involved (see the API's MangaCalendarController
/// for why the bookmark-matching approach was dropped). The feed is server-cached (~30 min), so
/// there's no need to react to bookmark sync events here.</summary>
public partial class MangaCalendarPanel
{
    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;

    private bool _loading = true;
    private List<MangaCalendarItem> _items = [];
    private MangaCalendarView _view = MangaCalendarView.Month;
    private DateTime _anchor = DateTime.Today;

    protected override async Task OnInitializedAsync()
    {
        await LoadScheduleAsync();
    }

    private void OnMonthDaySelected(DateOnly date)
    {
        _anchor = date.ToDateTime(TimeOnly.MinValue);
        _view = MangaCalendarView.Day;
    }

    private async Task LoadScheduleAsync()
    {
        _loading = true;
        StateHasChanged();
        try
        {
            var response = await BookmarkService.GetMangaScheduleAsync();
            _items = MangaCalendarItem.Deduplicate(response.Entries.Select(MangaCalendarItem.FromEntry));
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load manhwa calendar: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    private void OnItemClicked(MangaCalendarItem item)
    {
        if (!string.IsNullOrEmpty(item.Url))
        {
            _ = JSRuntime.InvokeVoidAsync("open", item.Url, "_blank");
        }
    }

    private void SetView(MangaCalendarView view) => _view = view;

    private void GoPrev() => Shift(-1);
    private void GoNext() => Shift(1);

    private void Shift(int direction)
    {
        _anchor = _view switch
        {
            MangaCalendarView.Month => _anchor.AddMonths(direction),
            MangaCalendarView.Day => _anchor.AddDays(direction),
            _ => _anchor.AddDays(7 * direction)
        };
    }

    private string ToolbarEyebrow => _view switch
    {
        MangaCalendarView.Day => "Today",
        MangaCalendarView.Week => "Recent Chapters",
        _ => "Calendar"
    };

    private string ToolbarTitle => _view switch
    {
        MangaCalendarView.Month => _anchor.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
        MangaCalendarView.Day => _anchor.ToString("dddd, MMMM d", CultureInfo.CurrentCulture),
        _ => FormatWeekRange(_anchor)
    };

    private static string FormatWeekRange(DateTime anchor)
    {
        var start = anchor.AddDays(-(int)anchor.DayOfWeek);
        var end = start.AddDays(6);
        return start.Month == end.Month
            ? $"{start:MMMM d} – {end:d}"
            : $"{start:MMM d} – {end:MMM d}";
    }
}
