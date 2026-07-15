using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Backup;
using BookmarkManager.Contracts;
using BookmarkManager.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/backups")]
public sealed class BackupsController(
    IBackupService backupService,
    IHostApplicationLifetime lifetime,
    IOptions<BackupOptions> backupOptions) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BackupManifestDto>>> ListAsync(CancellationToken ct)
        => Ok(await backupService.GetBackupsAsync(ct));

    [HttpGet("stats")]
    public async Task<ActionResult<BackupStatsDto>> GetStatsAsync(CancellationToken ct)
        => Ok(await backupService.GetStatsAsync(ct));

    [HttpPost]
    public async Task<ActionResult<BackupManifestDto>> CreateAsync(CancellationToken ct)
    {
        try
        {
            var manifest = await backupService.CreateBackupAsync(BackupManifestTrigger.Manual, ct);
            if (manifest.Status == BackupManifestStatus.Failed)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, ApiProblem.Create(
                    StatusCodes.Status500InternalServerError,
                    "backup_failed",
                    "Backup failed",
                    "The backup could not be completed."));
            }

            return Created($"/api/backups/{manifest.Id}", manifest);
        }
        catch (BackupAlreadyRunningException)
        {
            return Conflict(ApiProblem.Create(
                StatusCodes.Status409Conflict,
                "backup_in_progress",
                "Backup already in progress",
                "Wait for the current backup to finish before starting another."));
        }
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult<BackupRestoreResultDto>> RestoreAsync(
        Guid id,
        [FromBody] RestoreBackupRequest? request,
        CancellationToken ct)
    {
        try
        {
            var result = await backupService.ScheduleRestoreAsync(id, request?.Confirm ?? string.Empty, ct);

            if (backupOptions.Value.StopHostAfterRestore)
            {
                HttpContext.Response.OnCompleted(() =>
                {
                    lifetime.StopApplication();
                    return Task.CompletedTask;
                });
            }

            return Accepted(result);
        }
        catch (BackupInvalidConfirmException ex)
        {
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                "invalid_confirm",
                "Invalid confirmation",
                ex.Message));
        }
        catch (BackupNotFoundException)
        {
            return NotFound(ApiProblem.Create(
                StatusCodes.Status404NotFound,
                "backup_not_found",
                "Backup not found"));
        }
        catch (BackupAlreadyRunningException)
        {
            return Conflict(ApiProblem.Create(
                StatusCodes.Status409Conflict,
                "backup_in_progress",
                "Backup already in progress",
                "Wait for the current backup to finish before starting a restore."));
        }
        catch (BackupRestoreException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, ApiProblem.Create(
                StatusCodes.Status500InternalServerError,
                "restore_failed",
                "Restore failed",
                ex.Message));
        }
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> DownloadAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var (stream, fileName) = await backupService.OpenBackupAsync(id, ct);
            return File(stream, "application/octet-stream", fileName);
        }
        catch (BackupNotFoundException)
        {
            return NotFound(ApiProblem.Create(
                StatusCodes.Status404NotFound,
                "backup_not_found",
                "Backup not found"));
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await backupService.DeleteBackupAsync(id, ct);
            return NoContent();
        }
        catch (BackupNotFoundException)
        {
            return NotFound(ApiProblem.Create(
                StatusCodes.Status404NotFound,
                "backup_not_found",
                "Backup not found"));
        }
    }
}
