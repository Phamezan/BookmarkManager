using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BookmarkManager.Api.Controllers;

/// <summary>Global "what manhwa is releasing" calendar - independent of the user's bookmarks.
/// Bookmark-matching was tried first (mirroring <see cref="AnimeCalendarController"/>) but the
/// user's library is large enough that per-title matching rarely found confident matches and left
/// the calendar empty; this just plots MangaDex's own Korean-origin (manhwa) chapter feed instead.</summary>
[ApiController]
[Route("api/manga-calendar")]
public class MangaCalendarController : ControllerBase
{
    private const int FeedLimit = 100;

    private readonly MangaDexLibraryProvider _mangaDex;

    public MangaCalendarController(MangaDexLibraryProvider mangaDex)
    {
        _mangaDex = mangaDex;
    }

    [HttpGet("schedule")]
    public async Task<ActionResult<MangaCalendarScheduleResponse>> GetScheduleAsync(CancellationToken ct)
    {
        var entries = await _mangaDex.GetLatestManhwaReleasesAsync(FeedLimit, ct);
        return Ok(new MangaCalendarScheduleResponse { Entries = entries.ToList() });
    }
}
