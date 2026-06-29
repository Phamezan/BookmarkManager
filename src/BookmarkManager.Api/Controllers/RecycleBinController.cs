using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RecycleBinController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;

    public RecycleBinController(AppDbContext db, IMapper mapper)
    {
        _db = db;
        _mapper = mapper;
    }

    [HttpGet]
    public async Task<List<RecycleBinItemDto>> GetAllAsync(CancellationToken ct)
    {
        var deleted = await _db.BookmarkNodes
            .Where(n => n.IsDeleted)
            .OrderByDescending(n => n.DeletedAt)
            .ToListAsync(ct);

        return deleted.Select(n => new RecycleBinItemDto
        {
            Id = n.Id,
            Title = n.Title,
            Url = n.Url,
            Type = n.Type,
            DeletedAt = n.DeletedAt ?? DateTime.MinValue,
            PurgeAfter = n.PurgeAfter ?? DateTime.MinValue,
            CanRestore = n.PurgeAfter > DateTime.UtcNow
        }).ToList();
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult> RestoreAsync(Guid id, CancellationToken ct)
    {
        var node = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == id && n.IsDeleted, ct);
        if (node is null) return NotFound();

        node.IsDeleted = false;
        node.DeletedAt = null;
        node.PurgeAfter = null;
        node.SyncState = SyncState.Pending;

        var payload = await BuildRestorePayloadAsync(node, ct);

        _db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Restore",
            BookmarkId = node.Id,
            BrowserNodeId = null,
            ExpectedVersion = node.Version,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
            Status = "Pending"
        });

        await _db.SaveChangesAsync(ct);
        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
        return NoContent();
    }

    private async Task<object> BuildRestorePayloadAsync(BookmarkNode node, CancellationToken ct)
    {
        var parentNode = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == node.ParentId, ct);
        var parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0";

        var payload = new
        {
            bookmarkId = node.Id,
            type = node.Type == NodeType.Folder ? "Folder" : "Bookmark",
            parentBrowserNodeId = parentBrowserNodeId,
            title = node.Title,
            url = node.Url,
            position = node.Position,
            children = node.Type == NodeType.Folder
                ? await BuildRestoreChildrenAsync(node.Id, ct)
                : null
        };
        return payload;
    }

    private async Task<List<object>> BuildRestoreChildrenAsync(Guid parentId, CancellationToken ct)
    {
        var children = await _db.BookmarkNodes
            .Where(n => n.ParentId == parentId && n.IsDeleted)
            .OrderBy(n => n.Position)
            .ToListAsync(ct);

        var list = new List<object>();
        foreach (var child in children)
        {
            child.IsDeleted = false;
            child.DeletedAt = null;
            child.PurgeAfter = null;
            child.SyncState = SyncState.Pending;

            var parentNode = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == child.ParentId, ct);
            var parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0";

            list.Add(new
            {
                bookmarkId = child.Id,
                type = child.Type == NodeType.Folder ? "Folder" : "Bookmark",
                parentBrowserNodeId = parentBrowserNodeId,
                title = child.Title,
                url = child.Url,
                position = child.Position,
                children = child.Type == NodeType.Folder
                    ? await BuildRestoreChildrenAsync(child.Id, ct)
                    : null
            });
        }
        return list;
    }
}
