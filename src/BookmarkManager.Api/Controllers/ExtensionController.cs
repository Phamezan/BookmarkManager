using BookmarkManager.Api.Infrastructure;
using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/extension")]
public class ExtensionController(IExtensionService extensionService) : ControllerBase
{
    [HttpPost("heartbeat")]
    public async Task<ActionResult<HeartbeatResponse>> HeartbeatAsync(
        [FromBody] HeartbeatRequest request, CancellationToken ct)
    {
        var response = await extensionService.HandleHeartbeatAsync(request, ct);
        return Ok(response);
    }

    [HttpPost("folders")]
    public async Task<ActionResult<FolderCatalogResponse>> UploadFoldersAsync(
        [FromBody] FolderCatalogRequest request, CancellationToken ct)
    {
        if (request.CatalogId == Guid.Empty)
        {
            return ApiProblem.Result(
                StatusCodes.Status400BadRequest, ApiProblem.ValidationCode,
                "Invalid request", "catalogId is required.");
        }

        var response = await extensionService.StoreFolderCatalogAsync(request, ct);
        return Accepted(response);
    }

    [HttpGet("config")]
    public async Task<ExtensionConfigDto> GetConfigAsync(CancellationToken ct)
        => await extensionService.GetConfigAsync(ct);

    [HttpGet("status")]
    public async Task<ExtensionStatusDto> GetStatusAsync(CancellationToken ct)
        => await extensionService.GetStatusAsync(ct);

    [HttpPost("commands/claim")]
    public async Task<ActionResult<ClaimResponse>> ClaimCommandsAsync(
        [FromBody] ClaimRequest request, CancellationToken ct)
    {
        var response = await extensionService.ClaimCommandsAsync(request, ct);
        return Ok(response);
    }

    [HttpPost("events")]
    public async Task<ActionResult<EventBatchResponse>> SendEventsAsync(
        [FromBody] EventBatchRequest request, CancellationToken ct)
    {
        if (request.BatchId == Guid.Empty)
        {
            return ApiProblem.Result(
                StatusCodes.Status400BadRequest, ApiProblem.ValidationCode,
                "Invalid request", "batchId is required.");
        }

        var response = await extensionService.SendEventsAsync(request, ct);
        return Accepted(response);
    }

    [HttpPost("snapshot")]
    public async Task<ActionResult<SnapshotResponseDto>> UploadSnapshotAsync(
        [FromBody] SnapshotRequestPayloadDto request, CancellationToken ct)
    {
        var response = await extensionService.UploadSnapshotAsync(request, ct);
        return Accepted(response);
    }

    [HttpPost("commands/{operationId:guid}/complete")]
    public async Task<IActionResult> CompleteCommandAsync(
        Guid operationId, [FromBody] CompletionRequest request, CancellationToken ct)
    {
        await extensionService.CompleteCommandAsync(operationId, request, ct);
        return Ok();
    }

    [HttpPost("reset")]
    public async Task<IActionResult> ResetAsync(CancellationToken ct)
    {
        await extensionService.ResetDatabaseAsync(ct);
        return Ok();
    }
}
