using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services;

public sealed partial class ExtensionService
{
    public async Task<EventBatchResponse> SendEventsAsync(EventBatchRequest request, CancellationToken ct)
    {
        var client = await GetOrCreateDefaultClientAsync(ct);

        var requestEventIds = request.Events.Select(e => e.EventId).ToList();
        var existing = await db.ExtensionEvents
            .Where(e => e.ExtensionClientId == client.Id && requestEventIds.Contains(e.EventId))
            .Select(e => e.EventId)
            .ToListAsync(ct);

        var existingIds = new HashSet<Guid>(existing);
        var accepted = new List<Guid>();
        var duplicates = new List<Guid>();

        foreach (var evt in request.Events)
        {
            if (existingIds.Contains(evt.EventId))
            {
                duplicates.Add(evt.EventId);
                continue;
            }

            db.ExtensionEvents.Add(new ExtensionEventEntry
            {
                Id = Guid.NewGuid(),
                EventId = evt.EventId,
                ExtensionClientId = client.Id,
                EventType = evt.EventType,
                BrowserNodeId = evt.BrowserNodeId,
                TrackedRootBrowserNodeId = evt.TrackedRootBrowserNodeId,
                OccurredAt = evt.OccurredAt,
                CausedByOperationId = evt.CausedByOperationId,
                PayloadJson = evt.Payload is not null ? JsonSerializer.Serialize(evt.Payload) : null,
                ReceivedAt = DateTime.UtcNow,
                ConfigVersion = request.ConfigVersion,
                BatchId = request.BatchId
            });

            accepted.Add(evt.EventId);
        }

        // Event-row persistence and projection apply must be atomic: the rows
        // make the batch "accepted" for the dedup check above, so a partial
        // failure would permanently ack a batch that never touched the
        // projection. The extension retries the whole batch on rollback.
        List<Guid> createdBookmarkIds;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.SaveChangesAsync(ct);
            createdBookmarkIds = await ApplyEventChangesAsync(request.Events, ct);
            await tx.CommitAsync(ct);
        }

        await AutoTagCreatedBookmarksAsync(createdBookmarkIds, ct);

        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();

        return new EventBatchResponse
        {
            BatchId = request.BatchId,
            AcceptedEventIds = accepted,
            DuplicateEventIds = duplicates,
            ConfigVersion = request.ConfigVersion
        };
    }

    /// <summary>
    /// Applies event changes to the projection. Returns ids of bookmarks
    /// created by this batch so auto-tagging (an external HTTP call) can run
    /// after the surrounding transaction commits.
    /// </summary>
    private async Task<List<Guid>> ApplyEventChangesAsync(List<ExtensionEventDto> events, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var createdBookmarkIds = new List<Guid>();

        // Events caused by commands this server issued must not be re-applied:
        // the projection was already updated when the command was enqueued.
        // The event rows are still persisted above for audit/dedup.
        var causedByIds = events
            .Where(e => e.CausedByOperationId is not null)
            .Select(e => e.CausedByOperationId!.Value)
            .Distinct()
            .ToList();
        var echoOperationIds = causedByIds.Count == 0
            ? new HashSet<Guid>()
            : (await db.ExtensionCommands
                .Where(c => causedByIds.Contains(c.OperationId)
                    && (c.Status == "Succeeded" || c.Status == "Leased"))
                .Select(c => c.OperationId)
                .ToListAsync(ct)).ToHashSet();

        var applicableEvents = events
            .Where(e => e.CausedByOperationId is null || !echoOperationIds.Contains(e.CausedByOperationId.Value))
            .ToList();

        var allIds = new HashSet<Guid>();
        var browserNodeIds = new HashSet<string>();
        foreach (var evt in applicableEvents)
        {
            if (!string.IsNullOrEmpty(evt.BrowserNodeId))
            {
                allIds.Add(CreateNodeId(evt.BrowserNodeId));
                browserNodeIds.Add(evt.BrowserNodeId);
            }

            if (evt.EventType == "Created" || evt.EventType == "Moved" || evt.EventType == "Reordered")
            {
                var raw = JsonSerializer.Serialize(evt.Payload);
                using var preview = JsonDocument.Parse(raw);
                var el = preview.RootElement;

                if (evt.EventType == "Created" && (el.TryGetProperty("node", out var nd) || el.TryGetProperty("Node", out nd)))
                {
                    if ((nd.TryGetProperty("parentBrowserNodeId", out var pbn) || nd.TryGetProperty("ParentBrowserNodeId", out pbn))
                        && pbn.ValueKind == JsonValueKind.String)
                    {
                        var pbnStr = pbn.GetString()!;
                        allIds.Add(CreateNodeId(pbnStr));
                        browserNodeIds.Add(pbnStr);
                    }
                }
                else if (evt.EventType == "Moved"
                    && (el.TryGetProperty("parentBrowserNodeId", out var mb) || el.TryGetProperty("ParentBrowserNodeId", out mb))
                    && mb.ValueKind == JsonValueKind.String)
                {
                    var mbStr = mb.GetString()!;
                    allIds.Add(CreateNodeId(mbStr));
                    browserNodeIds.Add(mbStr);
                }
                else if (evt.EventType == "Reordered"
                    && (el.TryGetProperty("orderedChildBrowserNodeIds", out var arr) || el.TryGetProperty("OrderedChildBrowserNodeIds", out arr))
                    && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in arr.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String))
                    {
                        var cStr = c.GetString()!;
                        allIds.Add(CreateNodeId(cStr));
                        browserNodeIds.Add(cStr);
                    }
                }
            }
        }

        var existingNodes = await db.BookmarkNodes
            .Where(n => allIds.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, ct);

        var existingByBrowserId = await db.BookmarkNodes
            .Where(n => n.BrowserNodeId != null && browserNodeIds.Contains(n.BrowserNodeId))
            .ToDictionaryAsync(n => n.BrowserNodeId!, ct);

        foreach (var evt in applicableEvents)
        {
            var nodeId = CreateNodeId(evt.BrowserNodeId);

            if (evt.EventType == "Created")
            {
                if (existingNodes.ContainsKey(nodeId) || (!string.IsNullOrEmpty(evt.BrowserNodeId) && existingByBrowserId.ContainsKey(evt.BrowserNodeId)))
                    continue;

                var raw = JsonSerializer.Serialize(evt.Payload);
                using var doc = JsonDocument.Parse(raw);
                var rootEl = doc.RootElement;
                var nodeProp = rootEl.TryGetProperty("node", out var tempNode) ? tempNode : rootEl.GetProperty("Node");

                var typeStr = (nodeProp.TryGetProperty("type", out var tempType) ? tempType : nodeProp.GetProperty("Type")).GetString();
                var title = (nodeProp.TryGetProperty("title", out var tempTitle) ? tempTitle : nodeProp.GetProperty("Title")).GetString() ?? "";
                var url = (nodeProp.TryGetProperty("url", out var tempUrl) ? tempUrl : nodeProp.GetProperty("Url")).GetString();
                var position = (nodeProp.TryGetProperty("position", out var tempPos) ? tempPos : nodeProp.GetProperty("Position")).GetInt32();
                var isProtected = (nodeProp.TryGetProperty("isProtected", out var tempProt) ? tempProt : nodeProp.GetProperty("IsProtected")).GetBoolean();
            
                string? parentBrowserNodeId = null;
                if (nodeProp.TryGetProperty("parentBrowserNodeId", out var tempPbn))
                    parentBrowserNodeId = tempPbn.GetString();
                else if (nodeProp.TryGetProperty("ParentBrowserNodeId", out var tempPbn2))
                    parentBrowserNodeId = tempPbn2.GetString();

                Guid? parentId = null;
                if (!string.IsNullOrEmpty(parentBrowserNodeId))
                {
                    var pid = CreateNodeId(parentBrowserNodeId);
                    if (existingNodes.TryGetValue(pid, out var pNode))
                        parentId = pNode.Id;
                    else if (existingByBrowserId.TryGetValue(parentBrowserNodeId, out var pNodeByBrowser))
                        parentId = pNodeByBrowser.Id;
                    else
                    {
                        var pDb = await db.BookmarkNodes.FirstOrDefaultAsync(n => n.BrowserNodeId == parentBrowserNodeId, ct);
                        if (pDb is not null)
                            parentId = pDb.Id;
                    }
                }

                var newNode = new BookmarkNode
                {
                    Id = nodeId,
                    ParentId = parentId,
                    Type = typeStr == "Folder" ? NodeType.Folder : NodeType.Bookmark,
                    Title = title,
                    Url = url,
                    Position = position,
                    IsProtected = isProtected,
                    SyncState = SyncState.Synced,
                    UpdatedAt = now,
                    BrowserNodeId = evt.BrowserNodeId,
                    ParentBrowserNodeId = parentBrowserNodeId
                };

                if (newNode.Type == NodeType.Bookmark)
                    BookmarkPlanToReadHeuristic.ApplyAutoStatus(newNode);

                if (newNode.Type == NodeType.Bookmark && string.IsNullOrEmpty(newNode.Tags))
                {
                    // Auto-tagging is an external HTTP call — deferred until
                    // after the transaction commits (see AutoTagCreatedBookmarksAsync).
                    createdBookmarkIds.Add(newNode.Id);
                }

                db.BookmarkNodes.Add(newNode);
                existingNodes[nodeId] = newNode;
                if (!string.IsNullOrEmpty(evt.BrowserNodeId))
                {
                    existingByBrowserId[evt.BrowserNodeId] = newNode;
                }
                continue;
            }

            BookmarkNode? existing = null;
            if (!string.IsNullOrEmpty(evt.BrowserNodeId) && existingByBrowserId.TryGetValue(evt.BrowserNodeId, out var nodeByBrowserId))
            {
                existing = nodeByBrowserId;
            }
            else if (existingNodes.TryGetValue(nodeId, out var nodeById))
            {
                existing = nodeById;
            }

            if (existing is null)
                continue;

            var payloadRaw = JsonSerializer.Serialize(evt.Payload);
            using var payload = JsonDocument.Parse(payloadRaw);
            var root = payload.RootElement;

            switch (evt.EventType)
            {
                case "Changed":
                    if ((root.TryGetProperty("title", out var t) || root.TryGetProperty("Title", out t)) && t.ValueKind != JsonValueKind.Null)
                        existing.Title = t.GetString() ?? "";
                    if (root.TryGetProperty("url", out var u) || root.TryGetProperty("Url", out u))
                    {
                        existing.Url = u.ValueKind == JsonValueKind.Null ? null : u.GetString();
                        if (existing.Type == NodeType.Bookmark)
                            BookmarkPlanToReadHeuristic.ApplyAutoStatus(existing);
                    }
                    existing.UpdatedAt = now;
                    break;

                case "Moved":
                    if ((root.TryGetProperty("parentBrowserNodeId", out var mp) || root.TryGetProperty("ParentBrowserNodeId", out mp)) && mp.ValueKind == JsonValueKind.String)
                    {
                        var parentBrowserId = mp.GetString()!;
                        var pid = CreateNodeId(parentBrowserId);
                        if (existingNodes.TryGetValue(pid, out var pNode))
                            existing.ParentId = pNode.Id;
                        else if (existingByBrowserId.TryGetValue(parentBrowserId, out var pNodeByBrowser))
                            existing.ParentId = pNodeByBrowser.Id;
                        else
                        {
                            var pDb = await db.BookmarkNodes.FirstOrDefaultAsync(n => n.BrowserNodeId == parentBrowserId, ct);
                            if (pDb is not null)
                                existing.ParentId = pDb.Id;
                            else
                                existing.ParentId = null;
                        }

                        existing.ParentBrowserNodeId = parentBrowserId;
                    }
                    if (root.TryGetProperty("position", out var pos) || root.TryGetProperty("Position", out pos))
                        existing.Position = pos.GetInt32();
                    existing.UpdatedAt = now;
                    break;

                case "Removed":
                    existing.IsDeleted = true;
                    existing.DeletedAt = now;
                    existing.UpdatedAt = now;
                    break;

                case "Reordered":
                {
                    var propName = root.TryGetProperty("orderedChildBrowserNodeIds", out var arr) ? "orderedChildBrowserNodeIds" : "OrderedChildBrowserNodeIds";
                    if (root.TryGetProperty(propName, out var reorderArr))
                    {
                        var childBrowserIds = reorderArr
                            .EnumerateArray()
                            .Select(c => c.GetString()!)
                            .ToList();

                        var childGuids = childBrowserIds.Select(id => CreateNodeId(id)).ToList();

                        var children = await db.BookmarkNodes
                            .Where(n => childGuids.Contains(n.Id) || (n.BrowserNodeId != null && childBrowserIds.Contains(n.BrowserNodeId)))
                            .ToListAsync(ct);

                        var orderedChildren = children
                            .OrderBy(c => {
                                var idx = childBrowserIds.IndexOf(c.BrowserNodeId ?? "");
                                if (idx >= 0) return idx;
                                return childGuids.IndexOf(c.Id);
                            })
                            .ToList();

                        for (int i = 0; i < orderedChildren.Count; i++)
                        {
                            orderedChildren[i].Position = i;
                            orderedChildren[i].UpdatedAt = now;
                        }
                    }
                    break;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return createdBookmarkIds;
    }

    /// <summary>
    /// Runs auto-tagging for bookmarks created by an event batch. Called after
    /// the batch transaction commits so external HTTP calls never hold the
    /// write transaction open. Failures are logged per node and never fail the
    /// already-committed batch.
    /// </summary>
    private async Task AutoTagCreatedBookmarksAsync(List<Guid> createdBookmarkIds, CancellationToken ct)
    {
        if (createdBookmarkIds.Count == 0) return;

        var changed = false;
        foreach (var nodeId in createdBookmarkIds)
        {
            var node = await db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct);
            if (node is null || node.Type != NodeType.Bookmark || !string.IsNullOrEmpty(node.Tags))
                continue;

            try
            {
                var folderPath = await Data.FolderHierarchy.BuildFolderPathAsync(db, node.ParentId, ct);
                var outcome = await bookmarkTagging.GetTagsWithCoverAsync(node.Title, node.Url, folderPath, BookmarkTagDomainDto.Auto, ct);
                if (outcome.Tags.Count > 0)
                {
                    node.Tags = string.Join(",", outcome.Tags);
                    changed = true;
                }

                // Prefer the matched provider's poster (AniList) over a later og:image
                // scrape — only fill an empty cover so we never clobber an existing one.
                if (!string.IsNullOrWhiteSpace(outcome.CoverImageUrl) && string.IsNullOrWhiteSpace(node.CoverImageUrl))
                {
                    node.CoverImageUrl = outcome.CoverImageUrl;
                    changed = true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto-tagging failed for created bookmark {NodeId}", nodeId);
            }
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

}
