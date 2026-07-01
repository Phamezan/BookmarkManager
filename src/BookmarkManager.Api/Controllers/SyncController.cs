using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SyncController : ControllerBase
{
    private readonly AppDbContext _db;

    public SyncController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("status")]
    public async Task<SyncStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var nodeCount = await _db.BookmarkNodes.CountAsync(n => !n.IsDeleted, ct);
        var pendingCount = await _db.BookmarkNodes
            .CountAsync(n => n.SyncState == SyncState.Pending, ct);

        var lastSyncAt = await _db.BookmarkNodes
            .Where(n => !n.IsDeleted)
            .MaxAsync(n => (DateTime?)n.UpdatedAt, ct);

        return new SyncStatusDto
        {
            TotalNodeCount = nodeCount,
            PendingSyncCount = pendingCount,
            LastSyncAt = lastSyncAt ?? DateTime.MinValue
        };
    }
}
