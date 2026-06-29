using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BookmarksController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;

    public BookmarksController(AppDbContext db, IMapper mapper)
    {
        _db = db;
        _mapper = mapper;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<BookmarkNodeDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var node = await _db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted, ct);
        if (node is null) return NotFound();
        return _mapper.Map<BookmarkNodeDto>(node);
    }

    [HttpGet("deleted")]
    public async Task<List<BookmarkNodeDto>> GetDeletedAsync(CancellationToken ct)
    {
        var nodes = await _db.BookmarkNodes
            .Where(n => n.IsDeleted)
            .OrderByDescending(n => n.DeletedAt)
            .ToListAsync(ct);
        return _mapper.Map<List<BookmarkNodeDto>>(nodes);
    }

    [HttpGet("favorites")]
    public async Task<List<BookmarkNodeDto>> GetFavoritesAsync(CancellationToken ct)
    {
        var nodes = await _db.BookmarkNodes
            .Where(n => !n.IsDeleted && n.IsFavorite)
            .OrderBy(n => n.Title)
            .ToListAsync(ct);
        return _mapper.Map<List<BookmarkNodeDto>>(nodes);
    }

    [HttpGet("{parentId:guid}/children")]
    public async Task<List<BookmarkNodeDto>> GetChildrenAsync(Guid parentId, CancellationToken ct)
    {
        var nodes = await _db.BookmarkNodes
            .Where(n => n.ParentId == parentId && !n.IsDeleted)
            .OrderBy(n => n.Position)
            .ToListAsync(ct);
        return _mapper.Map<List<BookmarkNodeDto>>(nodes);
    }

    [HttpPost("{parentId:guid}")]
    public async Task<ActionResult<BookmarkNodeDto>> CreateAsync(
        Guid parentId,
        [FromBody] CreateBookmarkRequest request,
        CancellationToken ct)
    {
        string parentBrowserNodeId = "0";
        if (parentId != Guid.Empty)
        {
            var parentExists = await _db.BookmarkNodes.AnyAsync(n => n.Id == parentId && !n.IsDeleted, ct);
            if (!parentExists) return NotFound("Parent folder not found");

            var parentNode = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == parentId, ct);
            parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0";
        }

        var isRoot = parentId == Guid.Empty;
        var maxPos = isRoot
            ? await _db.BookmarkNodes.Where(n => n.ParentId == null).MaxAsync(n => (int?)n.Position, ct) ?? -1
            : await _db.BookmarkNodes.Where(n => n.ParentId == parentId).MaxAsync(n => (int?)n.Position, ct) ?? -1;

        var node = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = parentId == Guid.Empty ? null : parentId,
            Type = request.Type,
            Title = request.Title,
            Url = request.Url,
            Position = maxPos + 1,
            SyncState = SyncState.Pending,
            Version = 1,
            UpdatedAt = DateTime.UtcNow
        };

        _db.BookmarkNodes.Add(node);

        var payload = new
        {
            type = node.Type == NodeType.Folder ? "Folder" : "Bookmark",
            parentBrowserNodeId = parentBrowserNodeId,
            title = node.Title,
            url = node.Url,
            position = node.Position
        };

        _db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Create",
            BookmarkId = node.Id,
            BrowserNodeId = null,
            ExpectedVersion = node.Version,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
            Status = "Pending"
        });

        await _db.SaveChangesAsync(ct);
        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
        return Ok(_mapper.Map<BookmarkNodeDto>(node));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<BookmarkNodeDto>> UpdateAsync(
        Guid id,
        [FromBody] UpdateBookmarkRequest request,
        CancellationToken ct)
    {
        var node = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted, ct);
        if (node is null) return NotFound();

        node.Title = request.Title;
        node.Url = request.Url;
        node.Version++;
        node.UpdatedAt = DateTime.UtcNow;
        node.SyncState = SyncState.Pending;

        var payload = new
        {
            title = node.Title,
            url = node.Url
        };

        _db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Update",
            BookmarkId = node.Id,
            BrowserNodeId = node.BrowserNodeId,
            ExpectedVersion = node.Version,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
            Status = "Pending"
        });

        await _db.SaveChangesAsync(ct);
        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
        return _mapper.Map<BookmarkNodeDto>(node);
    }

    [HttpPut("{id:guid}/metadata")]
    public async Task<ActionResult<BookmarkNodeDto>> UpdateMetadataAsync(
        Guid id,
        [FromBody] BookmarkMetadataDto metadata,
        CancellationToken ct)
    {
        var node = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted, ct);
        if (node is null) return NotFound();

        node.Category = metadata.Category;
        node.Status = metadata.Status;
        node.CurrentProgress = metadata.CurrentProgress;
        node.TotalProgress = metadata.TotalProgress;
        node.Tags = metadata.Tags is { Count: > 0 } ? string.Join(",", metadata.Tags) : null;
        node.Rating = metadata.Rating;
        node.Notes = metadata.Notes;
        node.IsFavorite = metadata.IsFavorite;
        node.CoverImageUrl = metadata.CoverImageUrl;
        node.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return _mapper.Map<BookmarkNodeDto>(node);
    }

    [HttpPut("{id:guid}/move/{newParentId:guid}")]
    public async Task<ActionResult<BookmarkNodeDto>> MoveAsync(Guid id, Guid newParentId, CancellationToken ct)
    {
        var node = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted, ct);
        if (node is null) return NotFound();

        var parentNode = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == newParentId, ct);
        var parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0";

        var maxPos = await _db.BookmarkNodes.Where(n => n.ParentId == newParentId).MaxAsync(n => (int?)n.Position, ct) ?? -1;

        node.ParentId = newParentId;
        node.Position = maxPos + 1;
        node.Version++;
        node.SyncState = SyncState.Pending;
        node.UpdatedAt = DateTime.UtcNow;

        var payload = new
        {
            parentBrowserNodeId = parentBrowserNodeId,
            position = node.Position
        };

        _db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Move",
            BookmarkId = node.Id,
            BrowserNodeId = node.BrowserNodeId,
            ExpectedVersion = node.Version,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
            Status = "Pending"
        });

        await _db.SaveChangesAsync(ct);
        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
        return _mapper.Map<BookmarkNodeDto>(node);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        var node = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted, ct);
        if (node is null) return NotFound();

        node.IsDeleted = true;
        node.DeletedAt = DateTime.UtcNow;
        node.PurgeAfter = DateTime.UtcNow.AddDays(30);
        node.SyncState = SyncState.Pending;

        if (node.Type == NodeType.Folder)
        {
            await MarkDeletedRecursiveAsync(node.Id, node.DeletedAt.Value, node.PurgeAfter.Value, ct);
        }

        var payload = new
        {
            recursive = true
        };

        _db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Delete",
            BookmarkId = node.Id,
            BrowserNodeId = node.BrowserNodeId,
            ExpectedVersion = node.Version,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
            Status = "Pending"
        });

        await _db.SaveChangesAsync(ct);
        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
        return NoContent();
    }

    [HttpPost("batch-delete")]
    public async Task<ActionResult> BatchDeleteAsync([FromBody] BatchDeleteRequest request, CancellationToken ct)
    {
        var nodes = await _db.BookmarkNodes
            .Where(n => request.Ids.Contains(n.Id) && !n.IsDeleted)
            .ToListAsync(ct);

        foreach (var node in nodes)
        {
            node.IsDeleted = true;
            node.DeletedAt = DateTime.UtcNow;
            node.PurgeAfter = DateTime.UtcNow.AddDays(30);
            node.SyncState = SyncState.Pending;

            if (node.Type == NodeType.Folder)
            {
                await MarkDeletedRecursiveAsync(node.Id, node.DeletedAt.Value, node.PurgeAfter.Value, ct);
            }

            var payload = new
            {
                recursive = true
            };

            _db.ExtensionCommands.Add(new ExtensionCommandEntry
            {
                Id = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                CommandType = "Delete",
                BookmarkId = node.Id,
                BrowserNodeId = node.BrowserNodeId,
                ExpectedVersion = node.Version,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
                CreatedAt = DateTime.UtcNow,
                Status = "Pending"
            });
        }

        await _db.SaveChangesAsync(ct);
        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
        return NoContent();
    }

    [HttpPut("reorder/{parentId:guid}")]
    public async Task<ActionResult> ReorderAsync(Guid parentId, [FromBody] List<ReorderRequest> items, CancellationToken ct)
    {
        var nodeIds = items.Select(i => i.Id).ToList();
        var nodes = await _db.BookmarkNodes
            .Where(n => nodeIds.Contains(n.Id) && n.ParentId == parentId)
            .ToListAsync(ct);

        var parentNode = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == parentId, ct);
        var parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0";

        foreach (var item in items)
        {
            var node = nodes.FirstOrDefault(n => n.Id == item.Id);
            if (node is not null)
            {
                node.Position = item.NewPosition;
                node.SyncState = SyncState.Pending;
            }
        }

        var orderedIds = items.OrderBy(i => i.NewPosition).Select(i => i.Id).ToList();
        var orderedChildBrowserNodeIds = orderedIds
            .Select(id => nodes.FirstOrDefault(n => n.Id == id)?.BrowserNodeId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        var payload = new
        {
            parentBrowserNodeId = parentBrowserNodeId,
            orderedChildBrowserNodeIds = orderedChildBrowserNodeIds
        };

        _db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Reorder",
            BookmarkId = parentId,
            BrowserNodeId = parentBrowserNodeId,
            ExpectedVersion = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
            Status = "Pending"
        });

        await _db.SaveChangesAsync(ct);
        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
        return NoContent();
    }



    private async Task MarkDeletedRecursiveAsync(Guid folderId, DateTime deletedAt, DateTime purgeAfter, CancellationToken ct)
    {
        var children = await _db.BookmarkNodes
            .Where(n => n.ParentId == folderId && !n.IsDeleted)
            .ToListAsync(ct);

        foreach (var child in children)
        {
            child.IsDeleted = true;
            child.DeletedAt = deletedAt;
            child.PurgeAfter = purgeAfter;
            child.SyncState = SyncState.Pending;

            if (child.Type == NodeType.Folder)
            {
                await MarkDeletedRecursiveAsync(child.Id, deletedAt, purgeAfter, ct);
            }
        }
    }
}
