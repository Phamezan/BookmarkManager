using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

public partial class BookmarksController
{
    private const int MaxProposalIdsPerRequest = 500;

    [HttpGet("url-migration/dead-domains")]
    public async Task<ActionResult<List<DeadDomainCandidateDto>>> GetDeadDomainCandidatesAsync(CancellationToken ct)
    {
        // Detection is report-only: the link checker flags IsLinkBroken in place
        // (no "Broken Links" folder moves anymore).
        var bookmarks = await _db.BookmarkNodes
            .Where(n => n.IsLinkBroken && n.Type == NodeType.Bookmark && !n.IsDeleted && n.Url != null)
            .ToListAsync(ct);

        var grouped = bookmarks
            .Select(b => TryGetHost(b.Url))
            .Where(h => h != null)
            .GroupBy(h => h!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new DeadDomainCandidateDto { Host = g.Key, BookmarkCount = g.Count() })
            .OrderByDescending(d => d.BookmarkCount)
            .ToList();

        return Ok(grouped);
    }

    [HttpPost("url-migration/run")]
    public ActionResult<UrlMigrationStatusDto> StartUrlMigration(
        [FromBody] StartUrlMigrationRequest request,
        [FromServices] UrlMigrationBackgroundJob job)
    {
        var host = request?.DeadHost?.Trim();
        if (!IsValidHost(host))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "DeadHost must be a valid hostname (no scheme, path, or whitespace).",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var suggestedHost = request!.SuggestedHost?.Trim();
        if (!string.IsNullOrEmpty(suggestedHost) && !IsValidHost(suggestedHost))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "SuggestedHost must be a valid hostname (no scheme, path, or whitespace).",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var enqueued = job.Enqueue(host!, request.Force, string.IsNullOrEmpty(suggestedHost) ? null : suggestedHost);
        if (!enqueued)
        {
            return Conflict(new ProblemDetails
            {
                Title = "A URL migration run is already in progress.",
                Status = StatusCodes.Status409Conflict
            });
        }

        return Accepted(job.GetStatus());
    }

    [HttpGet("url-migration/status")]
    public ActionResult<UrlMigrationStatusDto> GetUrlMigrationStatus([FromServices] UrlMigrationBackgroundJob job)
    {
        return Ok(job.GetStatus());
    }

    [HttpGet("url-migration/proposals")]
    public async Task<ActionResult<List<UrlMigrationProposalDto>>> GetUrlMigrationProposalsAsync(
        [FromQuery] Guid? runId,
        [FromQuery] string? status,
        CancellationToken ct)
    {
        var query = _db.UrlMigrationProposals.AsQueryable();

        if (runId.HasValue)
        {
            query = query.Where(p => p.RunId == runId.Value);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(p => p.Status == status);
        }

        var proposals = await query
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        var bookmarkIds = proposals.Select(p => p.BookmarkId).Distinct().ToList();
        var titlesById = await _db.BookmarkNodes
            .Where(b => bookmarkIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.Title, ct);

        var dtos = proposals.Select(p => new UrlMigrationProposalDto
        {
            Id = p.Id,
            BookmarkId = p.BookmarkId,
            BookmarkTitle = titlesById.TryGetValue(p.BookmarkId, out var title) ? title : string.Empty,
            OldUrl = p.OldUrl,
            ProposedUrl = p.ProposedUrl,
            ProposedHost = p.ProposedHost,
            SeriesName = p.SeriesName,
            ChapterNumber = p.ChapterNumber,
            Confidence = p.Confidence,
            Detail = p.Detail,
            Status = p.Status,
            CreatedAt = p.CreatedAt
        }).ToList();

        return Ok(dtos);
    }

    [HttpPost("url-migration/proposals/approve")]
    public async Task<ActionResult<DecideProposalsResponse>> ApproveUrlMigrationProposalsAsync(
        [FromBody] DecideProposalsRequest request,
        [FromServices] UrlMigrationApprovalService approvalService,
        CancellationToken ct)
    {
        var validationError = ValidateProposalIds(request?.ProposalIds);
        if (validationError != null)
        {
            return BadRequest(validationError);
        }

        var result = await approvalService.ApproveAsync(request!.ProposalIds, ct);
        return Ok(result);
    }

    [HttpPost("url-migration/proposals/reject")]
    public async Task<ActionResult<DecideProposalsResponse>> RejectUrlMigrationProposalsAsync(
        [FromBody] DecideProposalsRequest request,
        [FromServices] UrlMigrationApprovalService approvalService,
        CancellationToken ct)
    {
        var validationError = ValidateProposalIds(request?.ProposalIds);
        if (validationError != null)
        {
            return BadRequest(validationError);
        }

        var result = await approvalService.RejectAsync(request!.ProposalIds, ct);
        return Ok(result);
    }

    [HttpPost("url-migration/proposals/cancel")]
    public async Task<ActionResult<DecideProposalsResponse>> CancelUrlMigrationProposalsAsync(
        [FromBody] DecideProposalsRequest request,
        [FromServices] UrlMigrationApprovalService approvalService,
        CancellationToken ct)
    {
        var validationError = ValidateProposalIds(request?.ProposalIds);
        if (validationError != null)
        {
            return BadRequest(validationError);
        }

        var result = await approvalService.CancelAsync(request!.ProposalIds, ct);
        return Ok(result);
    }

    [HttpPost("url-migration/proposals/{id:guid}/manual")]
    public async Task<ActionResult<DecideProposalsResponse>> SetManualProposalUrlAsync(
        Guid id,
        [FromBody] SetManualProposalUrlRequest request,
        [FromServices] UrlMigrationApprovalService approvalService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Url) ||
            !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Url must be an absolute http/https URL.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var result = await approvalService.SetManualUrlAndApproveAsync(id, request.Url, ct);
        return Ok(result);
    }

    [HttpPost("url-migration/proposals/{id:guid}/revert")]
    public async Task<IActionResult> RevertUrlMigrationProposalAsync(
        Guid id,
        [FromServices] UrlMigrationApprovalService approvalService,
        CancellationToken ct)
    {
        var reverted = await approvalService.RevertAsync(id, ct);
        if (!reverted)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Proposal could not be reverted (not found, not Approved, or bookmark has no previous URL).",
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok();
    }

    private static ProblemDetails? ValidateProposalIds(List<Guid>? proposalIds)
    {
        if (proposalIds == null || proposalIds.Count == 0)
        {
            return new ProblemDetails
            {
                Title = "ProposalIds must contain at least one id.",
                Status = StatusCodes.Status400BadRequest
            };
        }

        if (proposalIds.Count > MaxProposalIdsPerRequest)
        {
            return new ProblemDetails
            {
                Title = $"ProposalIds cannot exceed {MaxProposalIdsPerRequest} per request.",
                Status = StatusCodes.Status400BadRequest
            };
        }

        return null;
    }

    private static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.Any(char.IsWhiteSpace) || host.Contains('/') || host.Contains('?') || host.Contains('\\'))
        {
            return false;
        }

        return Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }

    private static string? TryGetHost(string? url) => Infrastructure.UrlHelpers.TryGetHost(url);
}
