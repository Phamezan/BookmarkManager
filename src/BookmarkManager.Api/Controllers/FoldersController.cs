using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FoldersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;

    public FoldersController(AppDbContext db, IMapper mapper)
    {
        _db = db;
        _mapper = mapper;
    }

    [HttpGet("tree")]
    public async Task<List<FolderTreeNodeDto>> GetTreeAsync(CancellationToken ct)
    {
        var allFolders = await _db.BookmarkNodes
            .Where(n => n.Type == NodeType.Folder && !n.IsDeleted)
            .OrderBy(n => n.Position)
            .ToListAsync(ct);

        var bookmarkCounts = await _db.BookmarkNodes
            .Where(n => n.Type == NodeType.Bookmark && !n.IsDeleted && n.ParentId != null)
            .GroupBy(n => n.ParentId!.Value)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ParentId, g => g.Count, ct);

        return BuildTree(allFolders, null, bookmarkCounts);
    }

    [HttpPut("{id:guid}/move/{newParentId:guid}")]
    public async Task<ActionResult> MoveFolderAsync(Guid id, Guid newParentId, CancellationToken ct)
    {
        var folder = await _db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.Id == id && n.Type == NodeType.Folder && !n.IsDeleted, ct);
        if (folder is null) return NotFound();

        var parentNode = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == newParentId, ct);
        // "0" only when no parent node exists (true root); an unconfirmed parent
        // defers the command instead (see DeferredCommandHelper).
        var parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0";

        var maxPos = await _db.BookmarkNodes.Where(n => n.ParentId == newParentId).MaxAsync(n => (int?)n.Position, ct) ?? -1;

        folder.ParentId = newParentId;
        folder.Position = maxPos + 1;
        folder.SyncState = SyncState.Pending;
        folder.UpdatedAt = DateTime.UtcNow;

        var payload = new
        {
            parentBrowserNodeId = parentBrowserNodeId,
            position = folder.Position
        };

        _db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Move",
            BookmarkId = folder.Id,
            BrowserNodeId = folder.BrowserNodeId,
            ExpectedVersion = folder.Version,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
            Status = Services.DeferredCommandHelper.InitialStatus(parentNode)
        });

        await _db.SaveChangesAsync(ct);
        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
        return NoContent();
    }

    private static List<FolderTreeNodeDto> BuildTree(List<BookmarkNode> nodes, Guid? parentId, Dictionary<Guid, int> bookmarkCounts)
    {
        var result = new List<FolderTreeNodeDto>();

        foreach (var node in nodes.Where(n => n.ParentId == parentId))
        {
            var children = BuildTree(nodes, node.Id, bookmarkCounts);
            if (parentId is null && string.IsNullOrWhiteSpace(node.Title))
            {
                result.AddRange(children);
                continue;
            }

            result.Add(new FolderTreeNodeDto
            {
                Id = node.Id,
                Title = node.Title,
                BookmarkCount = bookmarkCounts.TryGetValue(node.Id, out var count) ? count : 0,
                Children = children
            });
        }

        return result;
    }
}
