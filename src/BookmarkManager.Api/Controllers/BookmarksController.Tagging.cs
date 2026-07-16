using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

public partial class BookmarksController
{
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
            folderPath = await FolderHierarchy.BuildFolderPathAsync(_db, request.FolderId.Value, ct);

        var resultMapping = await _bookmarkTagging.GetTagsForBatchAsync(request.Items, folderPath, request.Domain, ct);

        return Ok(new BookmarkManager.Contracts.BatchTagResponse
        {
            Tags = resultMapping.Tags,
            SuggestedTitles = resultMapping.SuggestedTitles
        });
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

    /// <summary>
    /// Saves tag edits and (optionally) title edits from the auto-tagger review page in
    /// one request. Tag-only saves stay manager-only metadata (no sync command, no
    /// broadcast). Title changes MUST go through <see cref="ApplyBookmarkProjectionUpdate"/>
    /// so they never diverge from Brave (see .cursor/commands/review-sync-change.md) —
    /// one SaveChanges + one broadcast for the whole batch either way.
    /// </summary>
    [HttpPost("tags/bulk-save")]
    public async Task<ActionResult> BulkSaveTagsAsync(
    [FromBody] BookmarkManager.Contracts.BulkSaveTagsRequest request,
    CancellationToken ct)
    {
    var nodeIds = new HashSet<Guid>(request.Tags.Keys);
    if (request.Titles is not null)
    {
        foreach (var id in request.Titles.Keys)
            nodeIds.Add(id);
    }

    if (nodeIds.Count == 0)
        return Ok();

    var nodes = await _db.BookmarkNodes
        .Where(n => nodeIds.Contains(n.Id) && !n.IsDeleted)
        .ToListAsync(ct);
    var nodesById = nodes.ToDictionary(n => n.Id);

    var anyChange = false;
    var anyTitleChange = false;

    foreach (var (bookmarkId, tags) in request.Tags)
    {
        if (!nodesById.TryGetValue(bookmarkId, out var node))
            continue;

        node.Tags = string.Join(",", tags);
        node.UpdatedAt = DateTime.UtcNow;

        // Rows the user actually touched are "Manual" provenance; untouched rows keep
        // the AI-suggested source. A null ManuallyEditedTagIds (older client / rerun
        // quick-edit path) means treat everything as Manual, preserving prior behavior.
        var source = request.ManuallyEditedTagIds is null || request.ManuallyEditedTagIds.Contains(bookmarkId)
            ? "Manual"
            : "Suggested";
        TagProvenanceWriter.Replace(_db, node.Id, tags.Select(t => (t, source)), confidence: null);
        anyChange = true;
    }

    if (request.Titles is not null)
    {
        foreach (var (bookmarkId, newTitle) in request.Titles)
        {
            if (!nodesById.TryGetValue(bookmarkId, out var node))
                continue;

            var trimmed = newTitle?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, node.Title, StringComparison.Ordinal))
                continue;

            ApplyBookmarkProjectionUpdate(node, trimmed, url: null);
            anyChange = true;
            anyTitleChange = true;
        }
    }

    if (anyChange)
    {
        await _db.SaveChangesAsync(ct);
        if (anyTitleChange)
            await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
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

    var folderPath = await FolderHierarchy.BuildFolderPathAsync(_db, node.ParentId, ct);
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

    [HttpPost("{folderId:guid}/ai-auto-tag")]
    public async Task<ActionResult<AiAutoTagSummaryDto>> AiAutoTagAsync(
    Guid folderId,
    [FromQuery] bool forceRefresh = false,
    CancellationToken ct = default)
    {
    var folderExists = await _db.BookmarkNodes.AnyAsync(
        n => n.Id == folderId && n.Type == NodeType.Folder && !n.IsDeleted,
        ct);
    if (!folderExists)
        return NotFound();

    var aiAutoTagging = HttpContext.RequestServices.GetRequiredService<AiBookmarkAutoTaggingService>();
    try
    {
        var summary = await aiAutoTagging.TagFolderAsync(folderId, forceRefresh, ct);
        return Ok(summary);
    }
    catch (HttpRequestException ex) when (ex.StatusCode is not null)
    {
        return Problem(
            title: "OpenRouter API error",
            statusCode: (int)ex.StatusCode,
            detail: $"OpenRouter returned {ex.StatusCode}.");
    }
    catch (InvalidOperationException ex)
    {
        return Problem(title: "AI auto-tagging failed", statusCode: 400, detail: ex.Message);
    }
    }

    [HttpPost("{folderId:guid}/ai-auto-tag/batch")]
    public async Task<ActionResult<AiAutoTagSummaryDto>> AiAutoTagBatchAsync(
    Guid folderId,
    AiAutoTagBatchRequestDto request,
    CancellationToken ct = default)
    {
    var folderExists = await _db.BookmarkNodes.AnyAsync(
        n => n.Id == folderId && n.Type == NodeType.Folder && !n.IsDeleted,
        ct);
    if (!folderExists)
        return NotFound();

    var maxCandidates = request.MaxCandidates <= 0 ? 25 : Math.Min(request.MaxCandidates, 50);
    var aiAutoTagging = HttpContext.RequestServices.GetRequiredService<AiBookmarkAutoTaggingService>();
    try
    {
        var summary = await aiAutoTagging.TagFolderAsync(
            folderId,
            request.ForceRefresh,
            maxCandidates,
            request.ExcludedBookmarkIds,
            ct);
        return Ok(summary);
    }
    catch (HttpRequestException ex) when (ex.StatusCode is not null)
    {
        return Problem(
            title: "OpenRouter API error",
            statusCode: (int)ex.StatusCode,
            detail: $"OpenRouter returned {ex.StatusCode}.");
    }
    catch (InvalidOperationException ex)
    {
        return Problem(title: "AI auto-tagging failed", statusCode: 400, detail: ex.Message);
    }
    }

    [HttpPost("rerun-tags")]
    public async Task<ActionResult<AiAutoTagSummaryDto>> RerunTagsAsync(
    [FromBody] RerunBookmarksRequestDto request,
    CancellationToken ct = default)
    {
    if (request.BookmarkIds.Count == 0)
        return Problem(title: "Invalid rerun request", statusCode: 400, detail: "No bookmark IDs provided.");

    var aiAutoTagging = HttpContext.RequestServices.GetRequiredService<AiBookmarkAutoTaggingService>();
    try
    {
        var summary = await aiAutoTagging.RerunBookmarksAsync(request.BookmarkIds, ct);
        return Ok(summary);
    }
    catch (HttpRequestException ex) when (ex.StatusCode is not null)
    {
        return Problem(
            title: "OpenRouter API error",
            statusCode: (int)ex.StatusCode,
            detail: $"OpenRouter returned {ex.StatusCode}.");
    }
    catch (InvalidOperationException ex)
    {
        return Problem(title: "AI rerun failed", statusCode: 400, detail: ex.Message);
    }
    }

    [HttpGet("{id:guid}/tag-provenance")]
    public async Task<ActionResult<List<TagProvenanceDto>>> GetTagProvenanceAsync(
    Guid id,
    CancellationToken ct = default)
    {
    var rows = await _db.TagProvenances
        .Where(p => p.BookmarkId == id)
        .OrderByDescending(p => p.CreatedAt)
        .ToListAsync(ct);

    return Ok(rows.Select(p => new TagProvenanceDto
    {
        Tag = p.Tag,
        Provider = p.Provider,
        Confidence = p.Confidence,
        CreatedAt = p.CreatedAt
    }).ToList());
    }

    /// <summary>
    /// Returns the distinct set of tags currently in use, with usage counts.
    /// Powers the tag-filter chips in the client.
    /// </summary>
    [HttpGet("tags")]
    public async Task<ActionResult<List<TagCountDto>>> GetTagsAsync([FromQuery] Guid? folderId, CancellationToken ct)
    {
    IQueryable<BookmarkNode> query = _db.BookmarkNodes
        .Where(n => !n.IsDeleted && n.Type == NodeType.Bookmark && n.Tags != null && n.Tags != "");

    if (folderId.HasValue)
    {
        var descendantIds = await FolderHierarchy.GetDescendantFolderIdsAsync(_db, folderId.Value, ct);
        descendantIds.Add(folderId.Value);

        query = query.Where(n => n.ParentId != null && descendantIds.Contains(n.ParentId.Value));
    }

    var rows = await query
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

}
