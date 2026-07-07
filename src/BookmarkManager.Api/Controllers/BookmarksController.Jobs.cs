using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Controllers;

public partial class BookmarksController
{
    [HttpPost("check-links")]
    public IActionResult TriggerLinkCheck([FromServices] BookmarkManager.Api.Services.LinkCheckerService linkChecker)
    {
    linkChecker.TriggerCheck();
    return Accepted();
    }

    [HttpGet("check-links/status")]
    public ActionResult<bool> GetLinkCheckStatus([FromServices] BookmarkManager.Api.Services.LinkCheckerService linkChecker)
    {
    return Ok(linkChecker.IsRunning);
    }

    // Slim, synchronous ManualFolder-only triage: moves bookmarks matching a dead host into the
    // "Broken Links" folder without touching their URLs. The AutoSearch path was replaced by the
    // URL Migrator v2 pipeline (see BookmarksController.Migration.cs); this endpoint no longer
    // accepts ActionType "AutoSearch".
    [HttpPost("triage-domain")]
    public async Task<ActionResult<TriageJobStatusDto>> TriageDomain(
    [FromBody] TriageDomainRequest request,
    [FromServices] ILogger<BookmarksController> logger,
    CancellationToken ct)
    {
    if (string.IsNullOrWhiteSpace(request.MatchBaseUrl))
    {
        return BadRequest(new ProblemDetails
        {
            Title = "Match base URL cannot be empty.",
            Status = StatusCodes.Status400BadRequest
        });
    }

    if (!string.Equals(request.ActionType, "ManualFolder", StringComparison.OrdinalIgnoreCase))
    {
        return BadRequest(new ProblemDetails
        {
            Title = "Only the ManualFolder triage action is supported here; use the URL Migrator for automatic URL replacement.",
            Status = StatusCodes.Status400BadRequest
        });
    }

    var deadHost = ExtractDomain(request.MatchBaseUrl);

    var bookmarks = await _db.BookmarkNodes
        .Where(n => n.Type == NodeType.Bookmark && !n.IsDeleted && n.Url != null)
        .ToListAsync(ct);

    var matched = bookmarks
        .Where(n => n.Url!.StartsWith(request.MatchBaseUrl, StringComparison.OrdinalIgnoreCase) ||
                    n.Url!.Contains(request.MatchBaseUrl, StringComparison.OrdinalIgnoreCase) ||
                    HostMatchesTriageDomain(n.Url!, deadHost))
        .ToList();

    if (matched.Count == 0)
    {
        return Ok(new TriageJobStatusDto
        {
            IsRunning = false,
            TotalFound = 0,
            SuccessfullyProcessed = 0,
            TargetFolder = BrokenLinksFolderHelper.FolderName,
            CurrentDomain = deadHost
        });
    }

    var folder = await BrokenLinksFolderHelper.GetOrCreateFolderAsync(_db, logger, ct);
    if (folder == null)
    {
        return Problem(
            title: "Could not find any root folder to place Broken Links under.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    var movedCount = await BrokenLinksFolderHelper.MoveBookmarksIntoFolderAsync(
        _db, folder, matched.Select(m => m.Id).ToList(), logger, ct);

    return Ok(new TriageJobStatusDto
    {
        IsRunning = false,
        TotalFound = matched.Count,
        SuccessfullyProcessed = movedCount,
        TargetFolder = BrokenLinksFolderHelper.FolderName,
        CurrentDomain = deadHost
    });
    }

    // Kept only so the (client-owned) Settings page polling call doesn't 404 now that domain
    // triage runs synchronously within the POST above; there is no longer a background job to
    // report progress for. See note to the UI agent about removing this polling call.
    [HttpGet("triage-domain/status")]
    public ActionResult<TriageJobStatusDto> GetTriageStatus()
    {
    return Ok(new TriageJobStatusDto { IsRunning = false });
    }

    private static string ExtractDomain(string url)
    {
    if (string.IsNullOrWhiteSpace(url)) return string.Empty;
    try
    {
        var uri = new Uri(url);
        return uri.Host;
    }
    catch
    {
        var clean = url.Replace("https://", string.Empty).Replace("http://", string.Empty);
        var idx = clean.IndexOf('/');
        if (idx >= 0) clean = clean.Substring(0, idx);
        return clean;
    }
    }

    private static bool HostMatchesTriageDomain(string url, string deadHost)
    {
    if (string.IsNullOrWhiteSpace(deadHost) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
    {
        return false;
    }

    return uri.Host.Equals(deadHost, StringComparison.OrdinalIgnoreCase) ||
           uri.Host.EndsWith("." + deadHost, StringComparison.OrdinalIgnoreCase);
    }

    [HttpPost("auto-tagger/run")]
    public IActionResult TriggerAutoTagger([FromServices] BookmarkManager.Api.Services.AutoTaggerBackgroundJob autoTagger)
    {
    autoTagger.Trigger();
    return Accepted(autoTagger.GetStatus());
    }

    [HttpGet("auto-tagger/status")]
    public ActionResult<AutoTaggerStatusDto> GetAutoTaggerStatus([FromServices] BookmarkManager.Api.Services.AutoTaggerBackgroundJob autoTagger)
    {
    return Ok(autoTagger.GetStatus());
    }

}
