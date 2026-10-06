using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

/// <summary>
/// Read-only feed for the Suwayomi Discover page. Serves the merged, week-paged Action listing and
/// the background refresh status. Never mutates Suwayomi.
/// </summary>
[ApiController]
[Route("api/discover")]
public sealed class DiscoverController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DiscoverFeedBackgroundService _feed;
    private readonly DiscoverLibraryProgressProvider _progress;

    public DiscoverController(
        AppDbContext db,
        DiscoverFeedBackgroundService feed,
        DiscoverLibraryProgressProvider progress)
    {
        _db = db;
        _feed = feed;
        _progress = progress;
    }

    [HttpGet]
    public async Task<ActionResult<DiscoverFeedDto>> GetAsync(
        [FromQuery] int week = 0,
        [FromQuery] string? type = "all",
        [FromQuery] bool hideLibrary = false,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var window = DiscoverWindowCalculator.ForWeek(week, now);
        var typeFilter = ParseType(type);

        var windowRows = await _db.DiscoverChapters
            .Where(c => c.UploadedAt > window.Start && c.UploadedAt <= window.End)
            .Select(c => new { c.SeriesId, c.MangaId, c.SourceName, c.UploadedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        var seriesIds = windowRows.Select(r => r.SeriesId).Distinct().ToList();
        var series = await _db.DiscoverSeriesSet
            .Where(s => seriesIds.Contains(s.Id))
            .ToListAsync(ct).ConfigureAwait(false);

        if (typeFilter is { } filterType)
        {
            series = series.Where(s => s.Type == filterType).ToList();
        }

        var allowed = series.Select(s => s.Id).ToHashSet();
        var allowedRows = windowRows.Where(r => allowed.Contains(r.SeriesId)).ToList();

        // The series link is the source with the newest chapter in the window.
        var latestBySeries = allowedRows
            .GroupBy(r => r.SeriesId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.UploadedAt).First());

        var chosenMangaIds = latestBySeries.Values.Select(r => r.MangaId).Distinct().ToList();
        var chapterRows = chosenMangaIds.Count == 0
            ? []
            : await _db.DiscoverChapters
                .Where(c => chosenMangaIds.Contains(c.MangaId) && c.UploadedAt <= window.End)
                .Select(c => new { c.MangaId, c.ChapterNumber, c.Name, c.UploadedAt, c.SourceOrder })
                .ToListAsync(ct).ConfigureAwait(false);

        var chaptersByManga = chapterRows
            .GroupBy(c => c.MangaId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.ChapterNumber).Take(3).ToList());

        var sourcesBySeries = allowedRows
            .GroupBy(r => r.SeriesId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(r => r.SourceName)
                    .OrderByDescending(sg => sg.Max(r => r.UploadedAt))
                    .Select(sg => sg.Key)
                    .ToList());

        var progress = await _progress.GetAsync(ct).ConfigureAwait(false);

        var items = new List<DiscoverItemDto>();
        foreach (var s in series.OrderByDescending(s => latestBySeries[s.Id].UploadedAt))
        {
            var latest = latestBySeries[s.Id];
            var progressValue = progress.TryGetValue(s.TitleKey, out var pv) ? pv : (DiscoverProgressValue?)null;
            if (hideLibrary && progressValue is not null)
            {
                continue;
            }

            var chapters = chaptersByManga.TryGetValue(latest.MangaId, out var rows) ? rows : [];
            items.Add(new DiscoverItemDto
            {
                TitleKey = s.TitleKey,
                Title = s.Title,
                Type = DiscoverTypeResolver.ToApiValue(s.Type),
                CoverMangaId = s.CoverMangaId > 0 ? s.CoverMangaId : latest.MangaId,
                SeriesMangaId = latest.MangaId,
                Sources = sourcesBySeries.TryGetValue(s.Id, out var sources) ? sources : [],
                Chapters = chapters.Select(c => new DiscoverChapterDto
                {
                    Number = c.ChapterNumber,
                    Name = c.Name,
                    UploadedAt = c.UploadedAt,
                    MangaId = latest.MangaId,
                    SourceOrder = c.SourceOrder,
                    IsNew = DiscoverWindowCalculator.IsNew(c.UploadedAt, now)
                }).ToList(),
                Progress = progressValue is { } value
                    ? new DiscoverProgressDto { Read = value.Read, Latest = value.Latest }
                    : null
            });
        }

        var hasOlder = await _db.DiscoverChapters.AnyAsync(c => c.UploadedAt < window.Start, ct).ConfigureAwait(false);

        return Ok(new DiscoverFeedDto
        {
            WeekStart = window.Start,
            WeekEnd = window.End,
            HasOlder = hasOlder,
            LastRunAt = _feed.LastRunAt,
            Items = items
        });
    }

    [HttpGet("status")]
    public ActionResult<DiscoverStatusDto> GetStatus() => Ok(new DiscoverStatusDto
    {
        LastRunAt = _feed.LastRunAt,
        Running = _feed.IsRunning,
        Sources = _feed.GetSourceStatuses()
            .Select(s => new DiscoverSourceStatusDto { Name = s.Name, State = s.State, Detail = s.Detail })
            .ToList()
    });

    private static DiscoverSeriesType? ParseType(string? value) => (value ?? "all").Trim().ToLowerInvariant() switch
    {
        "manhwa" => DiscoverSeriesType.Manhwa,
        "manhua" => DiscoverSeriesType.Manhua,
        "manga" => DiscoverSeriesType.Manga,
        _ => null
    };
}
