using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

public partial class BookmarksController
{
    [HttpGet("suggest-tags")]
    public ActionResult<List<string>> SuggestTags(
    [FromQuery] string title,
    [FromQuery] string? url,
    [FromServices] BookmarkManager.Api.Services.TagExtractorService tagExtractor)
    {
    var tags = tagExtractor.ExtractTags(title ?? string.Empty, url);
    return Ok(tags);
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

    [HttpGet("recommendations")]
    public async Task<ActionResult<List<BookmarkNodeDto>>> GetRecommendationsAsync(
    [FromQuery] Guid[] folderIds, [FromQuery] int count = 30, CancellationToken ct = default)
    {
    if (folderIds.Length == 0) return new List<BookmarkNodeDto>();

    var allFolderIds = new HashSet<Guid>(folderIds);
    foreach (var folderId in folderIds)
    {
        var descendants = await GetDescendantFolderIdsAsync(folderId, ct);
        allFolderIds.UnionWith(descendants);
    }

    var matchingIds = await _db.BookmarkNodes
        .Where(n => n.Type == NodeType.Bookmark && !n.IsDeleted && n.ParentId != null && allFolderIds.Contains(n.ParentId.Value))
        .Select(n => n.Id)
        .ToListAsync(ct);

    var sampledIds = matchingIds
        .OrderBy(_ => Random.Shared.Next())
        .Take(count)
        .ToList();

    var nodes = await _db.BookmarkNodes
        .Where(n => sampledIds.Contains(n.Id))
        .ToListAsync(ct);

    return _mapper.Map<List<BookmarkNodeDto>>(nodes);
    }

    [HttpPost("{id:guid}/archive")]
    public async Task<ActionResult<BookmarkNodeDto>> ArchiveAsync(Guid id, CancellationToken ct)
    {
    var node = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted, ct);
    if (node is null) return NotFound();

    var rootFolder = await _db.BookmarkNodes
        .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && n.ParentId == null && !n.IsDeleted, ct);
    if (rootFolder == null)
    {
        rootFolder = await _db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && !n.IsDeleted, ct);
    }
    if (rootFolder == null) return BadRequest("No parent folder found.");

    var archiveFolder = await _db.BookmarkNodes
        .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && n.ParentId == rootFolder.Id && n.Title == "Archive" && !n.IsDeleted, ct);

    if (archiveFolder == null)
    {
        var maxPosRoot = await _db.BookmarkNodes
            .Where(n => n.ParentId == rootFolder.Id)
            .MaxAsync(n => (int?)n.Position, ct) ?? -1;

        archiveFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = rootFolder.Id,
            Type = NodeType.Folder,
            Title = "Archive",
            Position = maxPosRoot + 1,
            SyncState = SyncState.Pending,
            Version = 1,
            UpdatedAt = DateTime.UtcNow
        };
        _db.BookmarkNodes.Add(archiveFolder);

        var createPayload = new
        {
            parentId = rootFolder.BrowserNodeId ?? "1",
            title = "Archive"
        };
        _db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Create",
            BookmarkId = archiveFolder.Id,
            BrowserNodeId = null,
            ExpectedVersion = 0,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(createPayload),
            CreatedAt = DateTime.UtcNow,
            Status = "Pending"
        });
    }

    var maxPosArchive = await _db.BookmarkNodes
        .Where(n => n.ParentId == archiveFolder.Id)
        .MaxAsync(n => (int?)n.Position, ct) ?? -1;

    node.ParentId = archiveFolder.Id;
    node.Position = maxPosArchive + 1;
    node.SyncState = SyncState.Pending;
    node.UpdatedAt = DateTime.UtcNow;

    // If the Archive folder was just created above, its BrowserNodeId is still null —
    // the extension hasn't confirmed it yet. Queuing a Move now would fall back to a
    // wrong parent. Skip it; ExtensionService.Commands.cs enqueues the Move automatically
    // once the folder's "Create" command completes and its real BrowserNodeId is known.
    if (!string.IsNullOrEmpty(archiveFolder.BrowserNodeId))
    {
        var movePayload = new
        {
            parentBrowserNodeId = archiveFolder.BrowserNodeId,
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
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(movePayload),
            CreatedAt = DateTime.UtcNow,
            Status = "Pending"
        });
    }

    await _db.SaveChangesAsync(ct);
    await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();

    return _mapper.Map<BookmarkNodeDto>(node);
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

}
