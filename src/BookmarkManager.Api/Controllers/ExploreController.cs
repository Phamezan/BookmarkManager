using BookmarkManager.Api.Data;
using BookmarkManager.Api.Infrastructure;
using BookmarkManager.Api.Services.Explore;
using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

/// <summary>
/// Backend of the Suwayomi Explore page: browses the AniList manga catalog, knows the user's
/// Suwayomi library and saved / dismissed list, and hands a picked series to Suwayomi. AniList
/// failures answer 502 (<c>{ "error": "AniList unavailable" }</c>) and are never cached.
/// </summary>
[ApiController]
[Route("api/explore")]
public sealed class ExploreController : ControllerBase
{
    private const int MaxRandomPage = 30;
    private const int MaxTitleLength = 500;
    private const int MaxUrlLength = 2048;

    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal) { "Manga", "Manhwa", "Manhua" };

    private readonly AppDbContext _db;
    private readonly ExploreAniListClient _anilist;
    private readonly ExploreLibraryEnricher _enricher;
    private readonly ExploreShelfBuilder _shelves;
    private readonly ExploreSourceFinder _sources;
    private readonly ISuwayomiClient _suwayomi;

    public ExploreController(
        AppDbContext db,
        ExploreAniListClient anilist,
        ExploreLibraryEnricher enricher,
        ExploreShelfBuilder shelves,
        ExploreSourceFinder sources,
        ISuwayomiClient suwayomi)
    {
        _db = db;
        _anilist = anilist;
        _enricher = enricher;
        _shelves = shelves;
        _sources = sources;
        _suwayomi = suwayomi;
    }

    [HttpGet("browse")]
    public Task<IActionResult> BrowseAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var filter = ExploreFilter.FromQuery(Request.Query);
        var state = await _enricher.LoadStateAsync(ct).ConfigureAwait(false);
        var page = await _anilist.BrowseAsync(filter, ExploreQueries.BrowsePerPage, ct).ConfigureAwait(false);
        return Ok(new ExplorePageDto
        {
            Page = filter.Page,
            HasNextPage = page.HasNextPage,
            Total = page.Total,
            Items = ExploreLibraryEnricher.Prepare(page.Items, filter, state)
        });
    });

    [HttpGet("random")]
    public Task<IActionResult> RandomAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var filter = ExploreFilter.FromQuery(Request.Query) with { Page = 1, HideDismissed = true, HideLibrary = true };
        var state = await _enricher.LoadStateAsync(ct).ConfigureAwait(false);
        var first = await _anilist.BrowseAsync(filter, ExploreQueries.BrowsePerPage, ct).ConfigureAwait(false);

        var pageNumber = Random.Shared.Next(1, Math.Min(Math.Max(first.LastPage, 1), MaxRandomPage) + 1);
        var items = pageNumber == 1
            ? first.Items
            : (await _anilist.BrowseAsync(filter with { Page = pageNumber }, ExploreQueries.BrowsePerPage, ct)
                .ConfigureAwait(false)).Items;

        var candidates = ExploreLibraryEnricher.Prepare(items, filter, state);
        if (candidates.Count == 0 && pageNumber != 1)
        {
            candidates = ExploreLibraryEnricher.Prepare(first.Items, filter, state);
        }

        return candidates.Count == 0
            ? NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Nothing matches these filters."))
            : Ok(candidates[Random.Shared.Next(candidates.Count)]);
    });

    [HttpGet("shelves")]
    public Task<IActionResult> ShelvesAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        var state = await _enricher.LoadStateAsync(ct).ConfigureAwait(false);
        return Ok(await _shelves.BuildAsync(state, ct).ConfigureAwait(false));
    });

    [HttpGet("for-you")]
    public Task<IActionResult> ForYouAsync([FromQuery] int page = 1, CancellationToken ct = default) => GuardAsync(async () =>
    {
        var pageNumber = Math.Max(page, 1);
        var state = await _enricher.LoadStateAsync(ct).ConfigureAwait(false);
        var ranked = await _shelves.RankForYouAsync(state, ct).ConfigureAwait(false);

        var items = ranked
            .Skip((pageNumber - 1) * ExploreForYouRecommender.PageSize)
            .Take(ExploreForYouRecommender.PageSize)
            .ToList();
        ExploreLibraryEnricher.Apply(items, state);
        return Ok(new ExplorePageDto
        {
            Page = pageNumber,
            HasNextPage = ranked.Count > pageNumber * ExploreForYouRecommender.PageSize,
            Total = ranked.Count,
            Items = items
        });
    });

    [HttpGet("series/{anilistId:int}")]
    public Task<IActionResult> GetSeriesAsync(int anilistId, CancellationToken ct) => GuardAsync(async () =>
    {
        var detail = await _anilist.GetDetailAsync(anilistId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Series not found."));
        }

        var state = await _enricher.LoadStateAsync(ct).ConfigureAwait(false);
        ExploreLibraryEnricher.Apply(
            [detail, .. detail.Relations.Select(r => r.Item), .. detail.Recommendations], state);
        return Ok(detail);
    });

    [HttpGet("series/{anilistId:int}/sources")]
    public Task<IActionResult> GetSourcesAsync(int anilistId, CancellationToken ct) => GuardAsync(async () =>
    {
        var hits = await _sources.FindAsync(anilistId, ct).ConfigureAwait(false);
        return hits is null
            ? NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Series not found."))
            : Ok(hits);
    });

    [HttpGet("meta")]
    public Task<IActionResult> GetMetaAsync(CancellationToken ct)
        => GuardAsync(async () => Ok(await _anilist.GetMetaAsync(ct).ConfigureAwait(false)));

    [HttpPost("library")]
    public async Task<IActionResult> AddToLibraryAsync([FromBody] ExploreAddToLibraryRequest? request, CancellationToken ct)
    {
        if (request is null || request.MangaId <= 0)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "mangaId must be positive.");
        }

        try
        {
            await _suwayomi.AddToLibraryAsync(request.MangaId, ct).ConfigureAwait(false);
            return NoContent();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Suwayomi unavailable" });
        }
    }

    [HttpGet("list")]
    public async Task<IActionResult> GetListAsync([FromQuery] string? state, CancellationToken ct)
    {
        var wanted = state?.Trim().ToLowerInvariant();
        if (wanted is not (ExploreListEntry.Saved or ExploreListEntry.Dismissed))
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "state must be 'saved' or 'dismissed'.");
        }

        var entries = await _db.ExploreListEntries
            .AsNoTracking()
            .Where(e => e.State == wanted)
            .OrderByDescending(e => e.UpdatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        var items = entries.Select(e => new ExploreItemDto
        {
            AnilistId = e.AnilistId,
            Title = e.Title,
            CoverUrl = e.CoverUrl,
            Type = e.Type
        }).ToList();

        ExploreLibraryEnricher.Apply(items, await _enricher.LoadStateAsync(ct).ConfigureAwait(false));
        return Ok(items);
    }

    [HttpPut("list/{anilistId:int}")]
    public async Task<IActionResult> PutListAsync(
        int anilistId, [FromBody] ExploreListUpdateRequest? request, CancellationToken ct)
    {
        var requested = request?.State?.Trim().ToLowerInvariant();
        if (requested is not null && requested is not (ExploreListEntry.Saved or ExploreListEntry.Dismissed))
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "state must be 'saved', 'dismissed' or null.");
        }

        var entry = await _db.ExploreListEntries.FirstOrDefaultAsync(e => e.AnilistId == anilistId, ct).ConfigureAwait(false);
        if (requested is null)
        {
            if (entry is not null)
            {
                _db.ExploreListEntries.Remove(entry);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return NoContent();
        }

        if (entry is null)
        {
            entry = new ExploreListEntry { AnilistId = anilistId };
            _db.ExploreListEntries.Add(entry);
        }

        entry.State = requested;
        entry.Title = Truncate(request!.Title?.Trim() ?? string.Empty, MaxTitleLength);
        entry.CoverUrl = request.CoverUrl is { } url && url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? Truncate(url, MaxUrlLength)
            : null;
        entry.Type = request.Type is { } type && KnownTypes.Contains(type) ? type : "Unknown";
        entry.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return NoContent();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>Runs an action body, mapping an AniList outage to the 502 contract.</summary>
    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (ExploreUnavailableException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "AniList unavailable" });
        }
    }
}
