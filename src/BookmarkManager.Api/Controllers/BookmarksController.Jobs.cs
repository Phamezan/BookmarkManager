using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    [HttpPost("triage-domain")]
    public IActionResult TriageDomain(
    [FromBody] TriageDomainRequest request,
    [FromServices] BookmarkManager.Api.Services.DomainTriageBackgroundJob triageJob)
    {
    if (string.IsNullOrWhiteSpace(request.MatchBaseUrl))
    {
        return BadRequest(new ProblemDetails
        {
            Title = "Match base URL cannot be empty.",
            Status = StatusCodes.Status400BadRequest
        });
    }

    var enqueued = triageJob.Enqueue(request);
    if (!enqueued)
    {
        return Conflict(new ProblemDetails
        {
            Title = "A domain triage job is already running.",
            Status = StatusCodes.Status409Conflict
        });
    }

    var status = triageJob.GetStatus();
    return Accepted(new TriageJobStatusDto
    {
        IsRunning = status.IsRunning,
        TotalFound = status.TotalFound,
        SuccessfullyProcessed = status.SuccessfullyProcessed,
        TargetFolder = status.TargetFolder,
        CurrentDomain = status.CurrentDomain,
        ErrorMessage = status.ErrorMessage
    });
    }

    [HttpGet("triage-domain/status")]
    public ActionResult<TriageJobStatusDto> GetTriageStatus(
    [FromServices] BookmarkManager.Api.Services.DomainTriageBackgroundJob triageJob)
    {
    var status = triageJob.GetStatus();
    return Ok(new TriageJobStatusDto
    {
        IsRunning = status.IsRunning,
        TotalFound = status.TotalFound,
        SuccessfullyProcessed = status.SuccessfullyProcessed,
        TargetFolder = status.TargetFolder,
        CurrentDomain = status.CurrentDomain,
        ErrorMessage = status.ErrorMessage
    });
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
