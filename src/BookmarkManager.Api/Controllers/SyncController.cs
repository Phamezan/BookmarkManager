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
        var tracked = await _db.TrackedRoots.ToListAsync(ct);
        var nodeCount = await _db.BookmarkNodes.CountAsync(n => !n.IsDeleted, ct);
        var pendingCount = await _db.BookmarkNodes
            .CountAsync(n => n.SyncState == SyncState.Pending, ct);

        return new SyncStatusDto
        {
            TrackedRootCount = tracked.Count,
            TotalNodeCount = nodeCount,
            PendingSyncCount = pendingCount,
            LastSyncAt = tracked.Count > 0
                ? tracked.Max(t => t.LastSyncedAt)
                : DateTime.MinValue
        };
    }

    [HttpGet("roots")]
    public async Task<List<TrackedRootDto>> GetRootsAsync(CancellationToken ct)
    {
        var roots = await _db.TrackedRoots.ToListAsync(ct);
        return roots.Select(r => new TrackedRootDto
        {
            Id = r.Id,
            Title = r.Title,
            Url = r.Url,
            AddedAt = r.AddedAt,
            LastSyncedAt = r.LastSyncedAt
        }).ToList();
    }
}
