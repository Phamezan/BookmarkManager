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
    private readonly BookmarkManager.Api.Services.TagExtractorService _tagExtractor;
    private readonly BookmarkManager.Api.Services.BookmarkTaggingService _bookmarkTagging;

    public BookmarksController(
        AppDbContext db,
        IMapper mapper,
        BookmarkManager.Api.Services.TagExtractorService tagExtractor,
        BookmarkManager.Api.Services.BookmarkTaggingService bookmarkTagging)
    {
        _db = db;
        _mapper = mapper;
        _tagExtractor = tagExtractor;
        _bookmarkTagging = bookmarkTagging;
    }

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

    [HttpGet("stale")]
    public async Task<ActionResult<List<BookmarkNodeDto>>> GetStaleAsync([FromQuery] int days = 180, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var staleBookmarks = await _db.BookmarkNodes
            .Where(n => n.Type == NodeType.Bookmark && !n.IsDeleted && n.UpdatedAt <= cutoff)
            .OrderBy(n => n.UpdatedAt)
            .ToListAsync(ct);

        return _mapper.Map<List<BookmarkNodeDto>>(staleBookmarks);
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

        var movePayload = new
        {
            parentBrowserNodeId = archiveFolder.BrowserNodeId ?? "1",
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

    [HttpPost("{parentId:guid}")]
    public async Task<ActionResult<BookmarkNodeDto>> CreateAsync(
        Guid parentId,
        [FromBody] CreateBookmarkRequest request,
        CancellationToken ct)
    {
        string parentBrowserNodeId = "0";
        BookmarkNode? parentNode = null;
        if (parentId != Guid.Empty)
        {
            var parentExists = await _db.BookmarkNodes.AnyAsync(n => n.Id == parentId && !n.IsDeleted, ct);
            if (!parentExists) return NotFound("Parent folder not found");

            parentNode = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == parentId, ct);
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

        // Auto-tag new bookmarks created through the web UI/API.
        if (node.Type == NodeType.Bookmark)
        {
            var folderPath = await BuildFolderPathAsync(parentNode?.Id, ct);
            var autoTags = await _bookmarkTagging.GetTagsAsync(node.Title, node.Url, folderPath, BookmarkTagDomainDto.Auto, ct);
            if (autoTags.Count > 0)
                node.Tags = string.Join(",", autoTags);
        }

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

    [HttpPost("check-links")]
    public IActionResult TriggerLinkCheck([FromServices] BookmarkManager.Api.Services.LinkCheckerService linkChecker)
    {
        linkChecker.TriggerCheck();
        return Accepted();
    }

    [HttpGet("check-links/status")]
    public ActionResult<bool> GetLinkCheckStatus([FromServices] BookmarkManager.Api.Services.LinkCheckerService linkChecker)
    {
        return Ok(linkChecker.IsRunning);
    }

    [HttpPost("auto-tagger/run")]
    public IActionResult TriggerAutoTagger([FromServices] BookmarkManager.Api.Services.AutoTaggerBackgroundJob autoTagger)
    {
        autoTagger.Trigger();
        return Accepted(autoTagger.GetStatus());
    }

    [HttpGet("auto-tagger/status")]
    public ActionResult<AutoTaggerStatusDto> GetAutoTaggerStatus([FromServices] BookmarkManager.Api.Services.AutoTaggerBackgroundJob autoTagger)
    {
        return Ok(autoTagger.GetStatus());
    }

    // ── Tagging ─────────────────────────────────────────────────────────────

    [HttpPost("ai-tags/batch")]
    public async Task<ActionResult<BookmarkManager.Contracts.BatchTagResponse>> SuggestBatchTagsAsync(
        [FromBody] BookmarkManager.Contracts.BatchTagRequest request,
        CancellationToken ct)
    {
        try
        {
            var folderPath = request.FolderPath;
            if (string.IsNullOrWhiteSpace(folderPath) && request.FolderId.HasValue)
                folderPath = await BuildFolderPathAsync(request.FolderId.Value, ct);

            var resultMapping = await _bookmarkTagging.GetTagsForBatchAsync(request.Items, folderPath, request.Domain, ct);

            return Ok(new BookmarkManager.Contracts.BatchTagResponse { Tags = resultMapping });
        }
        catch (Exception ex)
        {
            return Problem(
                detail: ex.Message,
                statusCode: 500,
                title: "Batch Tagging Error"
            );
        }
    }

    [HttpPost("tags/bulk-save")]
    public async Task<ActionResult> BulkSaveTagsAsync(
        [FromBody] BookmarkManager.Contracts.BulkSaveTagsRequest request,
        CancellationToken ct)
    {
        if (request.Tags.Count > 0)
        {
            var ids = request.Tags.Keys.ToList();
            var nodes = await _db.BookmarkNodes.Where(n => ids.Contains(n.Id)).ToListAsync(ct);
            foreach (var node in nodes)
            {
                if (request.Tags.TryGetValue(node.Id, out var tags))
                {
                    node.Tags = string.Join(",", tags);
                    node.UpdatedAt = DateTime.UtcNow;
                }
            }
            await _db.SaveChangesAsync(ct);
        }
        return Ok();
    }

    [HttpGet("untagged-counts")]
    public async Task<ActionResult<Dictionary<Guid, int>>> GetUntaggedCountsAsync(CancellationToken ct)
    {
        var bookmarks = await _db.BookmarkNodes
            .Where(n => !n.IsDeleted && n.Type == NodeType.Bookmark && (n.Tags == null || n.Tags == ""))
            .ToListAsync(ct);

        var counts = bookmarks
            .Where(b => b.ParentId.HasValue)
            .GroupBy(b => b.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        return Ok(counts);
    }

    [HttpPost("{id:guid}/ai-tags")]
    public async Task<ActionResult<List<string>>> AiRetagAsync(
        Guid id,
        CancellationToken ct)
    {
        var node = await _db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted, ct);
        if (node is null) return NotFound();

        var folderPath = await BuildFolderPathAsync(node.ParentId, ct);
        var tags = await _bookmarkTagging.GetTagsAsync(node.Title, node.Url, folderPath, BookmarkTagDomainDto.Auto, ct);
        return Ok(tags);
    }

    /// <summary>
    /// Backfill tags on active bookmarks through the same folder-aware provider routing
    /// used by the manual Auto Tagger. This is explicit work and is never triggered
    /// by extension reconnect or snapshot restore.
    /// </summary>
    [HttpPost("retag-all")]
    public async Task<ActionResult<object>> RetagAllAsync(
        [FromQuery] bool overwrite = false,
        [FromServices] BookmarkManager.Api.Services.AutoTaggerService autoTagger = default!,
        CancellationToken ct = default)
    {
        var result = await autoTagger.ProcessAsync(overwrite, folderIds: null, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns the distinct set of tags currently in use, with usage counts.
    /// Powers the tag-filter chips in the client.
    /// </summary>
    [HttpGet("tags")]
    public async Task<ActionResult<List<TagCountDto>>> GetTagsAsync(CancellationToken ct)
    {
        var rows = await _db.BookmarkNodes
            .Where(n => !n.IsDeleted && n.Type == NodeType.Bookmark && n.Tags != null && n.Tags != "")
            .Select(n => n.Tags!)
            .ToListAsync(ct);

        var counts = rows
            .SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TagCountDto { Tag = g.Key, Count = g.Count() })
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Tag)
            .ToList();

        return Ok(counts);
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

    private async Task<string?> BuildFolderPathAsync(Guid? folderId, CancellationToken ct)
    {
        if (!folderId.HasValue)
            return null;

        var titles = new Stack<string>();
        var currentId = folderId;
        for (var depth = 0; currentId.HasValue && depth < 32; depth++)
        {
            var folder = await _db.BookmarkNodes
                .AsNoTracking()
                .Where(n => n.Id == currentId.Value && n.Type == NodeType.Folder && !n.IsDeleted)
                .Select(n => new { n.Title, n.ParentId })
                .FirstOrDefaultAsync(ct);

            if (folder is null)
                break;

            titles.Push(folder.Title);
            currentId = folder.ParentId;
        }

        return titles.Count == 0 ? null : string.Join(" / ", titles);
    }
}
