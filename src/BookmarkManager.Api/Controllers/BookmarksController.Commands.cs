using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

public partial class BookmarksController
{
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
        // "0" is a placeholder only; when the parent's BrowserNodeId is not yet
        // confirmed the command is enqueued Deferred and the payload rewritten
        // on promotion (see DeferredCommandHelper).
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
        Title = ClampBookmarkTitle(request.Title),
        Url = request.Url,
        Position = maxPos + 1,
        SyncState = SyncState.Pending,
        Version = 1,
        UpdatedAt = DateTime.UtcNow
    };

    // Auto-tag new bookmarks created through the web UI/API.
    if (node.Type == NodeType.Bookmark)
    {
        var folderPath = await FolderHierarchy.BuildFolderPathAsync(_db, parentNode?.Id, ct);
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
        Status = Services.DeferredCommandHelper.InitialStatus(parentNode)
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

    ApplyBookmarkProjectionUpdate(node, request.Title, request.Url);

    await _db.SaveChangesAsync(ct);
    await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
    return _mapper.Map<BookmarkNodeDto>(node);
    }

    /// <summary>Matches <c>BookmarkNode.Title</c> EF max length in AppDbContext.</summary>
    private const int MaxBookmarkTitleLength = 500;

    private static string ClampBookmarkTitle(string title)
    {
        var trimmed = title.Trim();
        return trimmed.Length <= MaxBookmarkTitleLength
            ? trimmed
            : trimmed[..MaxBookmarkTitleLength];
    }

    /// <summary>
    /// Single write path for title/url projection changes: bumps Version, marks the
    /// node Pending, and enqueues the matching "Update" ExtensionCommandEntry in the
    /// SAME unit of work. Callers must SaveChanges + BroadcastSyncAsync afterward.
    /// Used by both the single-bookmark edit dialog (UpdateAsync) and the auto-tagger
    /// review page's bulk title save so the two paths cannot drift (see
    /// .cursor/commands/review-sync-change.md).
    /// </summary>
    /// <param name="url">Pass null to leave the node's Url untouched (bulk title-only saves).</param>
    private void ApplyBookmarkProjectionUpdate(BookmarkNode node, string title, string? url)
    {
    title = ClampBookmarkTitle(title);

    if (node.PreviousTitle is null && !string.Equals(node.Title, title, StringComparison.Ordinal))
        node.PreviousTitle = node.Title;

    node.Title = title;
    if (url is not null)
        node.Url = url;
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
    // "0" only when no parent node exists (true root); an unconfirmed parent
    // defers the command instead (see DeferredCommandHelper).
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
        Status = Services.DeferredCommandHelper.InitialStatus(parentNode)
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
        await FolderHierarchy.MarkDeletedRecursiveAsync(_db, node.Id, node.DeletedAt.Value, node.PurgeAfter.Value, ct);
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
            await FolderHierarchy.MarkDeletedRecursiveAsync(_db, node.Id, node.DeletedAt.Value, node.PurgeAfter.Value, ct);
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
    // "0" only when no parent node exists (true root); an unconfirmed parent
    // defers the command instead (see DeferredCommandHelper).
    var parentBrowserNodeId = parentNode?.BrowserNodeId ?? "0";

    var sortedItems = items.OrderBy(i => i.NewPosition).ToList();
    for (int i = 0; i < sortedItems.Count; i++)
    {
        var item = sortedItems[i];
        var node = nodes.FirstOrDefault(n => n.Id == item.Id);
        if (node is not null)
        {
            node.Position = i;
            node.SyncState = SyncState.Pending;
            node.Version++;
        }
    }

    if (parentNode is not null)
    {
        parentNode.Version++;
    }

    var orderedIds = sortedItems.Select(i => i.Id).ToList();
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
        ExpectedVersion = parentNode?.Version ?? 1,
        PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
        CreatedAt = DateTime.UtcNow,
        Status = Services.DeferredCommandHelper.InitialStatus(parentNode),
    });

    await _db.SaveChangesAsync(ct);
    await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
    return NoContent();
    }
}
