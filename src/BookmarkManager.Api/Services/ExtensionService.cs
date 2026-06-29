using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services;

public sealed class ExtensionService(AppDbContext db) : IExtensionService
{
    public async Task<HeartbeatResponse> HandleHeartbeatAsync(HeartbeatRequest request, CancellationToken ct)
    {
        var client = await GetOrCreateDefaultClientAsync(ct);
        var now = DateTime.UtcNow;

        client.LastHeartbeatAt = now;
        client.ExtensionVersion = request.ExtensionVersion;
        client.BraveVersion = request.BraveVersion;
        client.LocalConfigVersion = request.LocalConfigVersion;
        client.PendingEventCount = request.PendingEventCount;
        client.LastSuccessfulSyncAt = request.LastSuccessfulSyncAt;

        var config = await GetOrCreateAppConfigAsync(ct);
        var trackedRootCount = await db.TrackedRoots.CountAsync(ct);

        await db.SaveChangesAsync(ct);

        return new HeartbeatResponse
        {
            ExtensionClientId = client.Id,
            ServerTime = now,
            ConfigVersion = config.ConfigVersion,
            PollIntervalSeconds = config.PollIntervalSeconds,
            TrackedRootCount = trackedRootCount
        };
    }

    // DESIGN CONSTRAINT NOTE:
    // This assumes exactly one extension client profile exists forever (V1 assumption).
    // If support for multiple concurrent Brave profiles is added in the future,
    // this single-client logic must be refactored and a database migration applied
    // to map events and heartbeats to specific extension client IDs.
    private async Task<ExtensionClient> GetOrCreateDefaultClientAsync(CancellationToken ct)
    {
        var client = await db.ExtensionClients.FirstOrDefaultAsync(ct);
        if (client is not null)
        {
            return client;
        }

        var now = DateTime.UtcNow;
        client = new ExtensionClient
        {
            Id = Guid.NewGuid(),
            FirstSeenAt = now,
            LastHeartbeatAt = now
        };
        db.ExtensionClients.Add(client);
        await db.SaveChangesAsync(ct);
        return client;
    }

    public async Task<FolderCatalogResponse> StoreFolderCatalogAsync(FolderCatalogRequest request, CancellationToken ct)
    {
        var client = await GetOrCreateDefaultClientAsync(ct);
        var existing = await db.FolderCatalogBatches
            .FirstOrDefaultAsync(b => b.ExtensionClientId == client.Id && b.CatalogId == request.CatalogId, ct);
        if (existing is not null)
        {
            return new FolderCatalogResponse
            {
                CatalogId = existing.CatalogId,
                AcceptedAt = existing.AcceptedAt
            };
        }

        var now = DateTime.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        db.FolderCatalogBatches.Add(new FolderCatalogBatch
        {
            Id = Guid.NewGuid(),
            CatalogId = request.CatalogId,
            ExtensionClientId = client.Id,
            CapturedAt = request.CapturedAt,
            AcceptedAt = now
        });

        var prior = await db.FolderCatalogEntries
            .Where(e => e.ExtensionClientId == client.Id)
            .ToListAsync(ct);
        if (prior.Count > 0)
        {
            db.FolderCatalogEntries.RemoveRange(prior);
        }

        foreach (var folder in request.Folders)
        {
            db.FolderCatalogEntries.Add(new FolderCatalogEntry
            {
                ExtensionClientId = client.Id,
                BrowserNodeId = folder.BrowserNodeId,
                ParentBrowserNodeId = folder.ParentBrowserNodeId,
                Title = folder.Title,
                Position = folder.Position,
                IsProtected = folder.IsProtected
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (!await db.TrackedRoots.AnyAsync(ct))
        {
            var rootFolders = await db.FolderCatalogEntries
                .Where(e => e.ExtensionClientId == client.Id && e.ParentBrowserNodeId == "0")
                .ToListAsync(ct);

            foreach (var rf in rootFolders)
            {
                db.TrackedRoots.Add(new TrackedRoot
                {
                    Id = Guid.NewGuid(),
                    Title = rf.Title,
                    BrowserNodeId = rf.BrowserNodeId,
                    AddedAt = DateTime.UtcNow,
                    LastSyncedAt = DateTime.MinValue
                });
            }
            await db.SaveChangesAsync(ct);

            var cfg = await GetOrCreateAppConfigAsync(ct);
            cfg.ConfigVersion++;
            cfg.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }



        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();

        return new FolderCatalogResponse
        {
            CatalogId = request.CatalogId,
            AcceptedAt = now
        };
    }

    public async Task<ExtensionStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var client = await db.ExtensionClients.FirstOrDefaultAsync(ct);
        if (client is null)
        {
            return new ExtensionStatusDto { IsConnected = false };
        }

        var config = await GetOrCreateAppConfigAsync(ct);
        var graceMs = config.PollIntervalSeconds * 1000 * 3;
        var isConnected = DateTime.UtcNow - client.LastHeartbeatAt <= TimeSpan.FromMilliseconds(graceMs);

        return new ExtensionStatusDto
        {
            IsConnected = isConnected,
            LastHeartbeatAt = client.LastHeartbeatAt,
            ExtensionVersion = client.ExtensionVersion,
            BraveVersion = client.BraveVersion
        };
    }

    public async Task<ExtensionConfigDto> GetConfigAsync(CancellationToken ct)
    {
        var config = await GetOrCreateAppConfigAsync(ct);
        var trackedRoots = await db.TrackedRoots
            .OrderBy(r => r.Title)
            .ToListAsync(ct);

        var requestSnapshot = trackedRoots.Any(r => r.LastSyncedAt == DateTime.MinValue);

        return new ExtensionConfigDto
        {
            ConfigVersion = config.ConfigVersion,
            PollIntervalSeconds = config.PollIntervalSeconds,
            TrackedRoots = trackedRoots.Select(r => new ExtensionTrackedRootDto
            {
                TrackedRootId = r.Id,
                BrowserNodeId = r.BrowserNodeId ?? string.Empty,
                DisplayName = r.Title,
                DefaultCategory = string.Empty
            }).ToList(),
            SnapshotRequest = requestSnapshot ? new SnapshotRequestDto
            {
                RequestId = Guid.NewGuid(),
                Reason = SnapshotReason.InitialImport
            } : null
        };
    }

    public async Task<ClaimResponse> ClaimCommandsAsync(ClaimRequest request, CancellationToken ct)
    {
        var client = await GetOrCreateDefaultClientAsync(ct);
        var now = DateTime.UtcNow;

        var pending = await db.ExtensionCommands
            .Where(c => c.Status == "Pending")
            .OrderBy(c => c.CreatedAt)
            .Take(request.MaxCommands > 0 ? request.MaxCommands : 10)
            .ToListAsync(ct);

        var commands = new List<ExtensionCommandDto>();
        foreach (var cmd in pending)
        {
            cmd.LeaseId = Guid.NewGuid();
            cmd.LeaseExpiresAt = now.AddSeconds(30);
            cmd.ClaimedByClientId = client.Id;

            commands.Add(new ExtensionCommandDto
            {
                OperationId = cmd.OperationId,
                LeaseId = cmd.LeaseId,
                LeaseExpiresAt = cmd.LeaseExpiresAt,
                CommandType = cmd.CommandType,
                BookmarkId = cmd.BookmarkId,
                BrowserNodeId = cmd.BrowserNodeId,
                ExpectedVersion = cmd.ExpectedVersion,
                CreatedAt = cmd.CreatedAt,
                Payload = cmd.PayloadJson is not null ? JsonSerializer.Deserialize<object>(cmd.PayloadJson) : null
            });
        }

        await db.SaveChangesAsync(ct);

        return new ClaimResponse { Commands = commands };
    }

    public async Task<EventBatchResponse> SendEventsAsync(EventBatchRequest request, CancellationToken ct)
    {
        var client = await GetOrCreateDefaultClientAsync(ct);

        var existing = await db.ExtensionEvents
            .Where(e => e.ExtensionClientId == client.Id && e.BatchId == request.BatchId)
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

        await db.SaveChangesAsync(ct);

        await ApplyEventChangesAsync(request.Events, ct);

        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();

        return new EventBatchResponse
        {
            BatchId = request.BatchId,
            AcceptedEventIds = accepted,
            DuplicateEventIds = duplicates,
            ConfigVersion = request.ConfigVersion
        };
    }

    private async Task ApplyEventChangesAsync(List<ExtensionEventDto> events, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var allIds = new HashSet<Guid>();
        var browserNodeIds = new HashSet<string>();
        foreach (var evt in events)
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

        foreach (var evt in events)
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
                        existing.Url = u.ValueKind == JsonValueKind.Null ? null : u.GetString();
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
    }

    private static readonly Guid BrowserNodeIdNamespace = Guid.Parse("8B3E4A1C-2D5F-4A7B-9C8E-1F6D0B3A2C4E");

    private static Guid CreateNodeId(string browserNodeId)
    {
        var nsBytes = BrowserNodeIdNamespace.ToByteArray();
        var inputBytes = Encoding.UTF8.GetBytes(browserNodeId);
        var combined = new byte[nsBytes.Length + inputBytes.Length];
        Buffer.BlockCopy(nsBytes, 0, combined, 0, nsBytes.Length);
        Buffer.BlockCopy(inputBytes, 0, combined, nsBytes.Length, inputBytes.Length);
        var hash = MD5.HashData(combined);
        hash[7] = (byte)((hash[7] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }

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

        await UpsertSnapshotTreeAsync(allNodes, ct);

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

        foreach (var root in request.Roots)
        {
            var trackedRoot = await db.TrackedRoots.FindAsync(new object[] { root.TrackedRootId }, ct);
            if (trackedRoot is not null)
            {
                trackedRoot.LastSyncedAt = now;
            }
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

    private async Task UpsertSnapshotTreeAsync(List<BookmarkNodeDto> allNodes, CancellationToken ct)
    {
        var ids = allNodes.Where(n => n.Id != Guid.Empty).Select(n => n.Id).Distinct().ToHashSet();
        if (ids.Count == 0) return;

        await UpsertCoreAsync(ids, allNodes, ct);
    }

    private async Task UpsertCoreAsync(HashSet<Guid> ids, List<BookmarkNodeDto> allNodes, CancellationToken ct)
    {
        var existing = await db.BookmarkNodes
            .Where(n => ids.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, ct);

        foreach (var node in allNodes.Where(n => n.Id != Guid.Empty))
        {
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
                existingNode.UpdatedAt = node.UpdatedAt;
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
                    UpdatedAt = node.UpdatedAt,
                    IsDeleted = node.IsDeleted,
                    DeletedAt = node.DeletedAt,
                    PurgeAfter = node.PurgeAfter,
                    BrowserNodeId = node.BrowserNodeId,
                    ParentBrowserNodeId = node.ParentBrowserNodeId
                });
            }
        }
    }

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

    public async Task CompleteCommandAsync(Guid operationId, CompletionRequest request, CancellationToken ct)
    {
        var cmd = await db.ExtensionCommands
            .FirstOrDefaultAsync(c => c.OperationId == operationId, ct);
        if (cmd is not null)
        {
            cmd.Status = request.Status;
            cmd.ErrorCode = request.ErrorCode;
            cmd.ErrorMessage = request.ErrorMessage;
            cmd.CompletedAt = DateTime.UtcNow;

            var node = await db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == cmd.BookmarkId, ct);
            if (node is not null)
            {
                if (request.Status == "Succeeded")
                {
                    node.SyncState = SyncState.Synced;
                    if (!string.IsNullOrEmpty(request.BrowserNodeId))
                    {
                        node.BrowserNodeId = request.BrowserNodeId;
                    }
                }
                else if (request.Status == "PermanentFailure" || request.Status == "Failed")
                {
                    node.SyncState = SyncState.Failed;
                }
            }

            if (request.CompletedNodeMappings.Count > 0)
            {
                var mappingNodeIds = request.CompletedNodeMappings.Select(m => m.BookmarkId).ToList();
                var mappingNodes = await db.BookmarkNodes
                    .Where(n => mappingNodeIds.Contains(n.Id))
                    .ToListAsync(ct);

                var batch = await db.SnapshotBatches
                    .OrderByDescending(b => b.AcceptedAt)
                    .FirstOrDefaultAsync(ct);

                foreach (var mapping in request.CompletedNodeMappings)
                {
                    var mNode = mappingNodes.FirstOrDefault(n => n.Id == mapping.BookmarkId);
                    if (mNode is not null)
                    {
                        mNode.BrowserNodeId = mapping.BrowserNodeId;
                        mNode.SyncState = SyncState.Synced;
                    }

                    if (batch is not null)
                    {
                        db.SnapshotNodeMappings.Add(new SnapshotNodeMapping
                        {
                            Id = Guid.NewGuid(),
                            SnapshotBatchId = batch.Id,
                            BookmarkId = mapping.BookmarkId,
                            BrowserNodeId = mapping.BrowserNodeId
                        });
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
        }
    }

    private async Task<AppConfig> GetOrCreateAppConfigAsync(CancellationToken ct)
    {
        var config = await db.AppConfig.FirstOrDefaultAsync(c => c.Id == AppConfigConstants.SingletonId, ct);
        if (config is not null)
        {
            return config;
        }

        config = new AppConfig
        {
            Id = AppConfigConstants.SingletonId,
            ConfigVersion = 1,
            PollIntervalSeconds = AppConfigConstants.DefaultPollIntervalSeconds,
            UpdatedAt = DateTime.UtcNow
        };
        db.AppConfig.Add(config);
        await db.SaveChangesAsync(ct);
        return config;
    }

    public async Task ResetDatabaseAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            db.BookmarkNodes.RemoveRange(db.BookmarkNodes);
            db.ExtensionCommands.RemoveRange(db.ExtensionCommands);
            db.ExtensionEvents.RemoveRange(db.ExtensionEvents);
            db.SnapshotNodeMappings.RemoveRange(db.SnapshotNodeMappings);
            db.SnapshotBatches.RemoveRange(db.SnapshotBatches);
            db.FolderCatalogEntries.RemoveRange(db.FolderCatalogEntries);
            db.FolderCatalogBatches.RemoveRange(db.FolderCatalogBatches);
            db.TrackedRoots.RemoveRange(db.TrackedRoots);
            db.ExtensionClients.RemoveRange(db.ExtensionClients);
            db.ActivityLog.RemoveRange(db.ActivityLog);
            db.AppConfig.RemoveRange(db.AppConfig);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
    }
}
