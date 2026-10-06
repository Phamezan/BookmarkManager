using System.Net.Mime;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Controllers;

/// <summary>
/// Suwayomi import (phase 1). Read-only discovery plus the import run and thumbnail proxy.
/// Approve/reject/revert reuse the existing URL Migrator proposal endpoints; the manual
/// re-match lives at <c>api/bookmarks/url-migration/proposals/{id}/suwayomi-match</c>.
/// </summary>
[ApiController]
[Route("api/suwayomi")]
public sealed class SuwayomiController : ControllerBase
{
    private const int ThumbnailCacheSeconds = 86400;

    private readonly AppDbContext _db;
    private readonly ISuwayomiClient _client;
    private readonly SuwayomiOptions _options;
    private readonly SuwayomiImportBackgroundJob _importJob;
    private readonly UrlMigrationBackgroundJob _migrationJob;

    public SuwayomiController(
        AppDbContext db,
        ISuwayomiClient client,
        IOptions<SuwayomiOptions> options,
        SuwayomiImportBackgroundJob importJob,
        UrlMigrationBackgroundJob migrationJob)
    {
        _db = db;
        _client = client;
        _options = options.Value;
        _importJob = importJob;
        _migrationJob = migrationJob;
    }

    [HttpGet("status")]
    public async Task<ActionResult<SuwayomiStatusDto>> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            var status = await _client.GetStatusAsync(ct).ConfigureAwait(false);
            var english = status.Sources.Count(s => string.Equals(s.Lang, "en", StringComparison.OrdinalIgnoreCase));
            return Ok(new SuwayomiStatusDto
            {
                Reachable = true,
                Version = status.Version,
                SourceCount = english,
                SearchOrder = _options.SourceOrder
            });
        }
        catch (Exception)
        {
            return Ok(new SuwayomiStatusDto
            {
                Reachable = false,
                SearchOrder = _options.SourceOrder
            });
        }
    }

    [HttpGet("import/preview")]
    public async Task<ActionResult<SuwayomiImportPreviewDto>> GetImportPreviewAsync(
        [FromQuery] Guid folderId, CancellationToken ct)
    {
        if (folderId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "folderId is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var preview = await SuwayomiScope.GetPreviewAsync(_db, folderId, ct).ConfigureAwait(false);
        if (preview is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Folder not found.",
                Status = StatusCodes.Status404NotFound
            });
        }

        return Ok(preview);
    }

    [HttpPost("import/run")]
    public async Task<ActionResult<SuwayomiImportStatusDto>> StartImportAsync(
        [FromBody] StartSuwayomiImportRequest request, CancellationToken ct)
    {
        if (request is null || request.FolderId == Guid.Empty)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "folderId is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (_migrationJob.GetStatus().IsRunning || _importJob.IsRunning)
        {
            return Conflict(new ProblemDetails
            {
                Title = "A migration or import run is already in progress.",
                Status = StatusCodes.Status409Conflict
            });
        }

        var resolved = await SuwayomiScope.ResolveFolderAsync(_db, request.FolderId, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return NotFound(new ProblemDetails
            {
                Title = "Folder not found.",
                Status = StatusCodes.Status404NotFound
            });
        }

        if (!_importJob.Enqueue(request.FolderId, resolved.Value.Title))
        {
            return Conflict(new ProblemDetails
            {
                Title = "A Suwayomi import run is already in progress.",
                Status = StatusCodes.Status409Conflict
            });
        }

        return Accepted(_importJob.GetStatus());
    }

    [HttpPost("import/cancel")]
    public ActionResult<SuwayomiImportStatusDto> CancelImport()
    {
        if (!_importJob.CancelActiveRun())
        {
            return Conflict(new ProblemDetails
            {
                Title = "No Suwayomi import run is active.",
                Status = StatusCodes.Status409Conflict
            });
        }

        return Accepted(_importJob.GetStatus());
    }

    [HttpGet("import/status")]
    public ActionResult<SuwayomiImportStatusDto> GetImportStatus() => Ok(_importJob.GetStatus());

    [HttpGet("search")]
    public async Task<ActionResult<List<SuwayomiSearchResultDto>>> SearchAsync(
        [FromQuery] string? q, [FromQuery] string? source, CancellationToken ct)
    {
        var query = q?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(new List<SuwayomiSearchResultDto>());
        }

        SuwayomiServerStatus server;
        try
        {
            server = await _client.GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Ok(new List<SuwayomiSearchResultDto>());
        }

        var english = server.Sources
            .Where(s => string.Equals(s.Lang, "en", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var all = server.Sources;

        string? requested = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        var names = requested is null ? _options.SourceOrder : [requested];

        var results = new List<SuwayomiSearchResultDto>();
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var match = english.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                continue;
            }

            IReadOnlyList<SuwayomiManga> hits;
            try
            {
                hits = await _client.SearchAsync(match.Id, query, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            results.AddRange(hits.Take(_options.MaxResultsPerSource).Select(h => new SuwayomiSearchResultDto
            {
                MangaId = h.Id,
                Title = h.Title,
                SourceName = match.Name,
                InLibrary = h.InLibrary
            }));
        }

        return Ok(results);
    }

    /// <summary>
    /// Proxies Suwayomi's plain-http thumbnail so the dashboard (which may be served over https)
    /// never emits a mixed-content request to the Suwayomi origin.
    /// </summary>
    [HttpGet("thumbnail/{mangaId:int}")]
    public async Task<IActionResult> GetThumbnailAsync(int mangaId, CancellationToken ct)
    {
        if (mangaId <= 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "mangaId must be positive.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            var thumbnail = await _client.GetThumbnailAsync(mangaId, ct).ConfigureAwait(false);
            Response.Headers.CacheControl = $"public, max-age={ThumbnailCacheSeconds}";
            return File(thumbnail.Content, thumbnail.ContentType);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Suwayomi thumbnail unavailable.",
                Detail = ex.Message,
                Status = StatusCodes.Status502BadGateway
            });
        }
    }
}
