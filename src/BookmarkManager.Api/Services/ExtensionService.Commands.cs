using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services;

public sealed partial class ExtensionService
{
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

                    if (node.Type == NodeType.Folder && (cmd.CommandType == "Create" || cmd.CommandType == "Restore"))
                    {
                        // Commands that were waiting on this folder's browser id
                        // can now be released to the extension.
                        await DeferredCommandHelper.PromoteDeferredCommandsAsync(db, node, ct);
                    }

                    if (node.Type == NodeType.Folder && cmd.CommandType == "Create")
                    {
                        var pendingChildren = await db.BookmarkNodes
                            .Where(n => n.ParentId == node.Id && !n.IsDeleted)
                            .ToListAsync(ct);

                        foreach (var child in pendingChildren)
                        {
                            child.ParentBrowserNodeId = node.BrowserNodeId;
                            child.SyncState = SyncState.Pending;

                            var hasPendingMove = await db.ExtensionCommands
                                .AnyAsync(c => c.BookmarkId == child.Id && c.CommandType == "Move" && c.Status == "Pending", ct);

                            if (!hasPendingMove && !string.IsNullOrEmpty(child.BrowserNodeId))
                            {
                                var movePayload = new
                                {
                                    parentBrowserNodeId = node.BrowserNodeId,
                                    position = child.Position
                                };
                                db.ExtensionCommands.Add(new ExtensionCommandEntry
                                {
                                    Id = Guid.NewGuid(),
                                    OperationId = Guid.NewGuid(),
                                    CommandType = "Move",
                                    BookmarkId = child.Id,
                                    BrowserNodeId = child.BrowserNodeId,
                                    ExpectedVersion = child.Version,
                                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(movePayload),
                                    CreatedAt = DateTime.UtcNow,
                                    Status = "Pending"
                                });
                            }
                        }
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

                        if (mNode.Type == NodeType.Folder)
                        {
                            await DeferredCommandHelper.PromoteDeferredCommandsAsync(db, mNode, ct);
                        }
                    }

                    if (batch is not null)
                    {
                        db.SnapshotNodeMappings.Add(new SnapshotNodeMapping
                        {
                            Id = Guid.NewGuid(),
                            SnapshotBatchId = batch.Id,
                            BookmarkId = mapping.BookmarkId,
                            BrowserNodeId = mapping.BrowserNodeId,
                            SourceCommandId = cmd.Id
                        });
                    }
                }
            }

            // Recursive restore creates every descendant in one adapter call and
            // reports them all via CompletedNodeMappings above, so any descendant
            // still Pending at this point (not just the folder's own status) was
            // genuinely missed by the extension — check after mappings are applied,
            // never before, or already-mapped children would be re-created.
            if (request.Status == "Succeeded" && node is not null
                && node.Type == NodeType.Folder && cmd.CommandType == "Restore")
            {
                await EnqueueCreateForUnmappedDescendantsAsync(db, node.Id, ct);
            }

            await db.SaveChangesAsync(ct);
            await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
        }
    }

    private async Task EnqueueCreateForUnmappedDescendantsAsync(AppDbContext db, Guid folderId, CancellationToken ct)
    {
        var pendingChildren = await db.BookmarkNodes
            .Where(n => n.ParentId == folderId && !n.IsDeleted && n.SyncState == SyncState.Pending)
            .ToListAsync(ct);

        foreach (var child in pendingChildren)
        {
            var parentNode = await db.BookmarkNodes.FirstOrDefaultAsync(n => n.Id == child.ParentId, ct);
            var parentBrowserId = parentNode?.BrowserNodeId;

            var payload = new
            {
                parentBrowserNodeId = parentBrowserId ?? "0",
                title = child.Title,
                url = child.Url,
                position = child.Position
            };

            db.ExtensionCommands.Add(new ExtensionCommandEntry
            {
                Id = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                CommandType = "Create",
                BookmarkId = child.Id,
                BrowserNodeId = null,
                ExpectedVersion = child.Version,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
                CreatedAt = DateTime.UtcNow,
                Status = DeferredCommandHelper.InitialStatus(parentNode)
            });

            if (child.Type == NodeType.Folder)
            {
                await EnqueueCreateForUnmappedDescendantsAsync(db, child.Id, ct);
            }
        }
    }
}
