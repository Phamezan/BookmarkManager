using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BookmarkManager.Api.Controllers;

public partial class BookmarksController
{
    [HttpGet("suggest-status")]
    public ActionResult<BookmarkStatusSuggestionDto> SuggestStatus([FromQuery] string? url)
    {
        var shouldMarkPlanToRead = BookmarkPlanToReadHeuristic.ShouldMarkPlanToRead(url);
        var status = shouldMarkPlanToRead ? BookmarkReadingStatus.PlanToRead : BookmarkReadingStatus.Ongoing;
        return Ok(new BookmarkStatusSuggestionDto
        {
            Status = status,
            IsSuggested = shouldMarkPlanToRead
        });
    }
    [HttpPut("{id:guid}/status")]
    public async Task<ActionResult<BookmarkNodeDto>> UpdateStatusAsync(
        Guid id,
        [FromBody] UpdateBookmarkStatusRequest request,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Status))
        {
            return Problem(
                detail: "Status cannot be empty.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Status Request");
        }

        var normalizedStatus = BookmarkReadingStatus.Normalize(request.Status);
        if (normalizedStatus is null)
        {
            return Problem(
                detail: $"Status '{request.Status}' is invalid. Approved statuses are Ongoing, Plan to Read, Completed, Dropped.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Lifecycle Status");
        }

        try
        {
            var updatedNode = await _statusProjectionService.UpdateStatusAsync(id, normalizedStatus, ct);
            return Ok(_mapper.Map<BookmarkNodeDto>(updatedNode));
        }
        catch (KeyNotFoundException ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status404NotFound,
                title: "Bookmark Not Found");
        }
        catch (InvalidOperationException ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Status Update Rejected");
        }
        catch (ArgumentException ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Argument");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while updating status for bookmark {BookmarkId}", id);
            return Problem(
                detail: "An unexpected error occurred while updating bookmark status.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Status Update Error");
        }
    }

    [HttpPost("bulk-status")]
    public async Task<ActionResult<BulkUpdateBookmarkStatusResponse>> BulkUpdateStatusAsync(
        [FromBody] BulkUpdateBookmarkStatusRequest request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Problem(
                detail: "Request payload cannot be empty.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Bulk Status Request");
        }

        if (string.IsNullOrWhiteSpace(request.Status))
        {
            return Problem(
                detail: "Target status cannot be empty.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Target Status");
        }

        var normalizedStatus = BookmarkReadingStatus.Normalize(request.Status);
        if (normalizedStatus is null)
        {
            return Problem(
                detail: $"Status '{request.Status}' is invalid. Approved statuses are Ongoing, Plan to Read, Completed, Dropped.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Lifecycle Status");
        }

        try
        {
            var response = await _statusProjectionService.BulkUpdateStatusAsync(request.BookmarkIds, normalizedStatus, ct);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during bulk status update for {Count} bookmarks", request.BookmarkIds.Count);
            return Problem(
                detail: "An unexpected error occurred during bulk status update.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Bulk Status Error");
        }
    }

    [HttpGet("{id:guid}/related-status-candidates")]
    public async Task<ActionResult<RelatedSeriesPreviewResponse>> GetRelatedStatusCandidatesAsync(
        Guid id,
        [FromQuery] string targetStatus,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetStatus))
        {
            return Problem(
                detail: "targetStatus query parameter is required.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing Target Status");
        }

        try
        {
            var response = await _statusProjectionService.GetRelatedStatusCandidatesAsync(id, targetStatus, ct);
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status404NotFound,
                title: "Bookmark Not Found");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error querying related series candidates for bookmark {BookmarkId}", id);
            return Problem(
                detail: "An unexpected error occurred while searching for related series bookmarks.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Candidate Search Error");
        }
    }
}
