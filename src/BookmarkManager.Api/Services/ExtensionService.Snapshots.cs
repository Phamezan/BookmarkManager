using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services;

public sealed partial class ExtensionService
{
    public async Task<SnapshotResponseDto> UploadSnapshotAsync(SnapshotRequestPayloadDto request, CancellationToken ct)
    {
        var client = await GetOrCreateDefaultClientAsync(ct);

        var duplicate = await db.SnapshotBatches
            .FirstOrDefaultAsync(b => b.RequestId == request.RequestId, ct);
        if (duplicate is not null)
        {
            var existingMappings = await db.SnapshotNodeMappings
                .Where(m => m.SnapshotBatchId == duplicate.Id)
                .ToListAsync(ct);

            return new SnapshotResponseDto
            {
                RequestId = request.RequestId,
                AcceptedAt = duplicate.AcceptedAt,
                Mappings = existingMappings.Select(m => new NodeMappingDto
                {
                    BookmarkId = m.BookmarkId,
                    BrowserNodeId = m.BrowserNodeId
                }).ToList()
            };
        }

        var now = DateTime.UtcNow;
        var batch = new SnapshotBatch
        {
            Id = Guid.NewGuid(),
            RequestId = request.RequestId,
            ExtensionClientId = client.Id,
            ConfigVersion = request.ConfigVersion,
            CapturedAt = request.CapturedAt,
            AcceptedAt = now
        };
        db.SnapshotBatches.Add(batch);

        var (allNodes, browserToBookmarkId) = ResolveNodeTree(request.Roots);

        await UpsertSnapshotTreeAsync(allNodes, batch.CapturedAt, ct);

        var mappings = new List<NodeMappingDto>();
        foreach (var kvp in browserToBookmarkId)
        {
            mappings.Add(new NodeMappingDto
            {
                BookmarkId = kvp.Value,
                BrowserNodeId = kvp.Key
            });
            db.SnapshotNodeMappings.Add(new SnapshotNodeMapping
            {
                Id = Guid.NewGuid(),
                SnapshotBatchId = batch.Id,
                BookmarkId = kvp.Value,
                BrowserNodeId = kvp.Key
            });
        }


        await db.SaveChangesAsync(ct);
        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();

        return new SnapshotResponseDto
        {
            RequestId = request.RequestId,
            AcceptedAt = now,
            Mappings = mappings
        };
    }

    private async Task UpsertSnapshotTreeAsync(List<BookmarkNodeDto> allNodes, DateTime capturedAt, CancellationToken ct)
    {
        var ids = allNodes.Where(n => n.Id != Guid.Empty).Select(n => n.Id).Distinct().ToHashSet();
        if (ids.Count == 0) return;

        // Fetch all active (non-deleted) nodes to check for orphans
        var activeNodes = await db.BookmarkNodes.Where(n => !n.IsDeleted).ToListAsync(ct);
        var parentToChildren = activeNodes.GroupBy(n => n.ParentId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.ToList());

        var dbDescendantIds = new HashSet<Guid>();
        var rootIncomingNodes = allNodes.Where(n => n.ParentId == null && n.Id != Guid.Empty);

        foreach (var root in rootIncomingNodes)
        {
            dbDescendantIds.Add(root.Id);
            CollectDescendants(root.Id, dbDescendantIds, parentToChildren);
        }

        var incomingIds = allNodes.Where(n => n.Id != Guid.Empty).Select(n => n.Id).ToHashSet();
        var missingNodes = activeNodes.Where(n => dbDescendantIds.Contains(n.Id) && !incomingIds.Contains(n.Id)).ToList();

        var now = DateTime.UtcNow;
        foreach (var node in missingNodes)
        {
            node.IsDeleted = true;
            node.DeletedAt = now;
            node.PurgeAfter = now.AddDays(30);
            node.UpdatedAt = now;
        }

        await UpsertCoreAsync(ids, allNodes, capturedAt, ct);
    }

    private static void CollectDescendants(Guid parentId, HashSet<Guid> result, Dictionary<Guid, List<BookmarkNode>> parentToChildren)
    {
        if (parentToChildren.TryGetValue(parentId, out var children))
        {
            foreach (var child in children)
            {
                if (result.Add(child.Id))
                {
                    CollectDescendants(child.Id, result, parentToChildren);
                }
            }
        }
    }

    private async Task UpsertCoreAsync(HashSet<Guid> ids, List<BookmarkNodeDto> allNodes, DateTime capturedAt, CancellationToken ct)
    {
        var effectiveCapturedAt = IsDefault(capturedAt) ? DateTime.UtcNow : capturedAt;

        var existing = await db.BookmarkNodes
            .Where(n => ids.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, ct);

        foreach (var node in allNodes.Where(n => n.Id != Guid.Empty))
        {
            var incomingIsDefault = IsDefault(node.UpdatedAt);
            var effectiveTimestamp = incomingIsDefault ? effectiveCapturedAt : node.UpdatedAt;

            if (existing.TryGetValue(node.Id, out var existingNode))
            {
                existingNode.ParentId = node.ParentId;
                existingNode.Type = node.Type;
                existingNode.Title = node.Title;
                existingNode.Url = node.Url;
                existingNode.Position = node.Position;
                existingNode.IsProtected = node.IsProtected;
                existingNode.SyncState = SyncState.Synced;
                existingNode.Version = node.Version;
                if (!(incomingIsDefault && !IsDefault(existingNode.UpdatedAt)))
                {
                    existingNode.UpdatedAt = effectiveTimestamp;
                }
                existingNode.IsDeleted = node.IsDeleted;
                existingNode.DeletedAt = node.DeletedAt;
                existingNode.PurgeAfter = node.PurgeAfter;
                existingNode.BrowserNodeId = node.BrowserNodeId;
                existingNode.ParentBrowserNodeId = node.ParentBrowserNodeId;
            }
            else
            {
                db.BookmarkNodes.Add(new BookmarkNode
                {
                    Id = node.Id,
                    ParentId = node.ParentId,
                    Type = node.Type,
                    Title = node.Title,
                    Url = node.Url,
                    Position = node.Position,
                    IsProtected = node.IsProtected,
                    SyncState = SyncState.Synced,
                    Version = node.Version,
                    UpdatedAt = effectiveTimestamp,
                    IsDeleted = node.IsDeleted,
                    DeletedAt = node.DeletedAt,
                    PurgeAfter = node.PurgeAfter,
                    BrowserNodeId = node.BrowserNodeId,
                    ParentBrowserNodeId = node.ParentBrowserNodeId
                });
            }
        }
    }

    private static bool IsDefault(DateTime value) => value == default(DateTime) || DateTime.MinValue.Equals(value);

    private static (List<BookmarkNodeDto> AllNodes, Dictionary<string, Guid> BrowserToBookmarkId) ResolveNodeTree(
        List<SnapshotRootPayloadDto> roots)
    {
        var allNodes = new List<BookmarkNodeDto>();
        foreach (var root in roots)
        {
            FlattenTree(root.Root, allNodes);
        }

        var browserToBookmarkId = new Dictionary<string, Guid>();
        foreach (var node in allNodes)
        {
            if (node.BrowserNodeId is not null && !browserToBookmarkId.ContainsKey(node.BrowserNodeId))
            {
                browserToBookmarkId[node.BrowserNodeId] = CreateNodeId(node.BrowserNodeId);
            }
        }

        foreach (var node in allNodes)
        {
            if (node.BrowserNodeId is not null && browserToBookmarkId.TryGetValue(node.BrowserNodeId, out var bookmarkId))
            {
                node.Id = bookmarkId;
            }

            if (node.ParentBrowserNodeId is not null && browserToBookmarkId.TryGetValue(node.ParentBrowserNodeId, out var parentId))
            {
                node.ParentId = parentId;
            }
            else
            {
                node.ParentId = null;
            }
        }

        return (allNodes, browserToBookmarkId);
    }

    private static void FlattenTree(BookmarkNodeDto node, List<BookmarkNodeDto> result)
    {
        result.Add(node);
        if (node.Children is not null)
        {
            foreach (var child in node.Children)
            {
                FlattenTree(child, result);
            }
        }
    }

}
