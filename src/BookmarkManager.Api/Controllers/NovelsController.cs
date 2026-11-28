using BookmarkManager.Api.Data;
using BookmarkManager.Api.Infrastructure;
using BookmarkManager.Api.Services.Novels;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/novels")]
public sealed class NovelsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly NovelService _novels;

    public NovelsController(AppDbContext db, NovelService novels)
    {
        _db = db;
        _novels = novels;
    }

    [HttpGet("sources")]
    public Task<IActionResult> GetSourcesAsync(CancellationToken ct) => GuardAsync(async () =>
        Ok(await _novels.GetEnabledSourcesAsync(ct).ConfigureAwait(false)));

    [HttpGet("catalog")]
    public async Task<IActionResult> GetCatalogAsync(
        [FromQuery] string? q,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        CancellationToken ct = default)
    {
        if (q is { Length: > NovelPathValidator.MaxQueryLength })
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "q is too long.");
        }

        return Ok(await _novels.GetCatalogAsync(q, status, page, ct).ConfigureAwait(false));
    }

    [HttpGet("popular")]
    public Task<IActionResult> GetPopularAsync(
        [FromQuery] string? source,
        [FromQuery] int page = 1,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (page is < 1 or > 50)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "page must be between 1 and 50.");
        }

        if (string.IsNullOrWhiteSpace(source) || !_novels.IsSourceEnabled(source))
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Novel source not found."));
        }

        var items = await _novels.GetPopularAsync(source, page, ct).ConfigureAwait(false);
        return items is null
            ? NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Novel source not found."))
            : Ok(items);
    });

    [HttpGet("search")]
    public Task<IActionResult> SearchAsync(
        [FromQuery] string? q,
        [FromQuery] string? source,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "q is required.");
        }

        if (q.Length > NovelPathValidator.MaxQueryLength)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "q is too long.");
        }

        if (!string.IsNullOrWhiteSpace(source) && !_novels.IsSourceEnabled(source))
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Novel source not found."));
        }

        var items = await _novels.SearchAsync(q, source, ct).ConfigureAwait(false);
        return items is null
            ? NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Novel source not found."))
            : Ok(items);
    });

    [HttpGet("novel")]
    public Task<IActionResult> GetNovelAsync(
        [FromQuery] string? source,
        [FromQuery] string? path,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(source) || !_novels.IsSourceEnabled(source))
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Novel source not found."));
        }

        var pathError = NovelPathValidator.ValidatePath(path);
        if (pathError is not null)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, pathError);
        }

        var detail = await _novels.GetNovelAsync(source, path!, ct).ConfigureAwait(false);
        return detail is null
            ? NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Novel source not found."))
            : Ok(detail);
    });

    [HttpGet("chapter")]
    public Task<IActionResult> GetChapterAsync(
        [FromQuery] string? source,
        [FromQuery] string? novelPath,
        [FromQuery] string? path,
        CancellationToken ct = default) => GuardAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(source) || !_novels.IsSourceEnabled(source))
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Novel source not found."));
        }

        var novelPathError = NovelPathValidator.ValidatePath(novelPath, "novelPath");
        if (novelPathError is not null)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, novelPathError);
        }

        var pathError = NovelPathValidator.ValidatePath(path);
        if (pathError is not null)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, pathError);
        }

        var result = await _novels.GetChapterAsync(source, novelPath!, path!, ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            ChapterLookupOutcome.SourceDisabled => NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Novel source not found.")),
            ChapterLookupOutcome.NotFound => NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Chapter not found.")),
            ChapterLookupOutcome.Locked => StatusCode(StatusCodes.Status403Forbidden, new { error = "Locked chapter" }),
            _ => Ok(result.Content)
        };
    });

    [HttpGet("library")]
    public async Task<IActionResult> GetLibraryAsync(CancellationToken ct)
    {
        var entries = await _db.NovelLibraryEntries
            .AsNoTracking()
            .OrderByDescending(e => e.LastReadAt != null)
            .ThenByDescending(e => e.LastReadAt)
            .ThenByDescending(e => e.UpdatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Ok(entries.Select(NovelService.MapLibraryEntry).ToList());
    }

    [HttpPut("library")]
    public async Task<IActionResult> UpsertLibraryAsync([FromBody] UpsertNovelLibraryRequest? request, CancellationToken ct)
    {
        if (request is null)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "Request body is required.");
        }

        var source = request.Source?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(source) || !_novels.IsSourceEnabled(source))
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Novel source not found."));
        }

        var pathError = NovelPathValidator.ValidatePath(request.NovelPath, "novelPath");
        if (pathError is not null)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, pathError);
        }

        var title = request.Title?.Trim() ?? string.Empty;
        if (title.Length == 0 || title.Length > NovelPathValidator.MaxTitleLength)
        {
            return ApiProblem.Result(
                StatusCodes.Status400BadRequest,
                ApiProblem.ValidationCode,
                $"title is required and must be at most {NovelPathValidator.MaxTitleLength} characters.");
        }

        var coverError = NovelPathValidator.ValidateOptionalHttpUrl(request.CoverUrl, "coverUrl");
        if (coverError is not null)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, coverError);
        }

        if (request.Status is { } st && !Enum.IsDefined(st))
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "Invalid status.");
        }

        var now = DateTime.UtcNow;
        var entry = await _db.NovelLibraryEntries
            .FirstOrDefaultAsync(e => e.Source == source && e.NovelPath == request.NovelPath, ct)
            .ConfigureAwait(false);

        if (entry is null)
        {
            entry = new NovelLibraryEntry
            {
                Id = Guid.NewGuid(),
                Source = source,
                NovelPath = request.NovelPath,
                Title = title,
                CoverUrl = string.IsNullOrWhiteSpace(request.CoverUrl) ? null : request.CoverUrl.Trim(),
                Status = request.Status ?? NovelReadingStatus.Reading,
                AddedAt = now,
                UpdatedAt = now
            };
            _db.NovelLibraryEntries.Add(entry);
        }
        else
        {
            entry.Title = title;
            if (request.CoverUrl is not null)
            {
                entry.CoverUrl = string.IsNullOrWhiteSpace(request.CoverUrl) ? null : request.CoverUrl.Trim();
            }
            if (request.Status is { } nextStatus)
            {
                entry.Status = nextStatus;
            }
            entry.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Ok(NovelService.MapLibraryEntry(entry));
    }

    [HttpPatch("library/{id:guid}")]
    public async Task<IActionResult> PatchLibraryStatusAsync(
        Guid id,
        [FromBody] UpdateNovelLibraryStatusRequest? request,
        CancellationToken ct)
    {
        if (request?.Status is not { } status || !Enum.IsDefined(status))
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "Valid status is required.");
        }

        var entry = await _db.NovelLibraryEntries.FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (entry is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Library entry not found."));
        }

        entry.Status = status;
        entry.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Ok(NovelService.MapLibraryEntry(entry));
    }

    [HttpPut("library/{id:guid}/progress")]
    public async Task<IActionResult> UpdateProgressAsync(
        Guid id,
        [FromBody] UpdateNovelLibraryProgressRequest? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "Request body is required.");
        }

        var pathError = NovelPathValidator.ValidatePath(request.ChapterPath, "chapterPath");
        if (pathError is not null)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, pathError);
        }

        if (request.ChapterName is { Length: > NovelPathValidator.MaxTitleLength })
        {
            return ApiProblem.Result(
                StatusCodes.Status400BadRequest,
                ApiProblem.ValidationCode,
                $"chapterName must be at most {NovelPathValidator.MaxTitleLength} characters.");
        }

        if (request.ChapterIndex < 0)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "chapterIndex must be non-negative.");
        }

        if (request.ChapterCount is < 0)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "chapterCount must be non-negative.");
        }

        if (double.IsNaN(request.ScrollPercent) || double.IsInfinity(request.ScrollPercent) || request.ScrollPercent < 0 || request.ScrollPercent > 1)
        {
            return ApiProblem.Result(StatusCodes.Status400BadRequest, ApiProblem.ValidationCode, "scrollPercent must be between 0 and 1.");
        }

        var entry = await _db.NovelLibraryEntries.FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (entry is null)
        {
            return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "Library entry not found."));
        }

        var now = DateTime.UtcNow;
        entry.LastChapterPath = request.ChapterPath;
        entry.LastChapterName = string.IsNullOrWhiteSpace(request.ChapterName) ? null : request.ChapterName.Trim();
        entry.LastChapterIndex = request.ChapterIndex;
        if (request.ChapterCount is { } count)
        {
            entry.ChapterCount = count;
        }
        entry.ScrollPercent = request.ScrollPercent;
        entry.LastReadAt = now;
        entry.UpdatedAt = now;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Ok(NovelService.MapLibraryEntry(entry));
    }

    [HttpDelete("library/{id:guid}")]
    public async Task<IActionResult> DeleteLibraryAsync(Guid id, CancellationToken ct)
    {
        var entry = await _db.NovelLibraryEntries.FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (entry is not null)
        {
            _db.NovelLibraryEntries.Remove(entry);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return NoContent();
    }

    private async Task<IActionResult> GuardAsync(Func<Task<IActionResult>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (NovelSourceUnavailableException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Novel source unavailable" });
        }
    }
}
