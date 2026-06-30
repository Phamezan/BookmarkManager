using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TrackedRootsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;

    public TrackedRootsController(AppDbContext db, IMapper mapper)
    {
        _db = db;
        _mapper = mapper;
    }

    [HttpGet]
    public async Task<List<TrackedRootDto>> GetAsync(CancellationToken ct)
    {
        var roots = await _db.TrackedRoots
            .OrderBy(r => r.Title)
            .ToListAsync(ct);
        return _mapper.Map<List<TrackedRootDto>>(roots);
    }

    [HttpPost]
    public async Task<ActionResult<TrackedRootDto>> AddAsync(
        [FromBody] CreateTrackedRootRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Title))
        {
            return BadRequest("Title is required.");
        }

        var now = DateTime.UtcNow;
        var root = new TrackedRoot
        {
            Id = Guid.NewGuid(),
            Title = request.Title.Trim(),
            Url = string.IsNullOrWhiteSpace(request.Url) ? null : request.Url,
            BrowserNodeId = request.BrowserNodeId,
            AddedAt = now,
            LastSyncedAt = DateTime.MinValue
        };

        _db.TrackedRoots.Add(root);
        await _db.SaveChangesAsync(ct);
        return Ok(_mapper.Map<TrackedRootDto>(root));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> RemoveAsync(Guid id, CancellationToken ct)
    {
        var root = await _db.TrackedRoots.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (root is null) return NotFound();

        _db.TrackedRoots.Remove(root);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/sync")]
    public async Task<ActionResult> SyncAsync(Guid id, CancellationToken ct)
    {
        var root = await _db.TrackedRoots.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (root is null) return NotFound();

        root.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/repair")]
    public async Task<ActionResult> RepairAsync(Guid id, CancellationToken ct)
    {
        var root = await _db.TrackedRoots.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (root is null) return NotFound();

        root.LastSyncedAt = DateTime.MinValue;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
