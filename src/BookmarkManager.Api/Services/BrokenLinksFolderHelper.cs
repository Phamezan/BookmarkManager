using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Infrastructure;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services;

/// <summary>
/// Shared "Broken Links" folder create/move logic, extracted from <see cref="LinkCheckerService"/>
/// so the deferred-move invariant (folder create and bookmark moves must wait for the folder's
/// <see cref="BookmarkNode.BrowserNodeId"/> to be confirmed by the extension) lives in one place.
/// Used by <see cref="LinkCheckerService"/> and the slim ManualFolder triage endpoint.
/// </summary>
public static class BrokenLinksFolderHelper
{
    public const string FolderName = "Broken Links";

    /// <summary>
    /// Finds the existing "Broken Links" folder or creates it (under the tracked root folder)
    /// if missing. When newly created, its <see cref="BookmarkNode.BrowserNodeId"/> is null until
    /// the extension confirms it — moves into it are enqueued Deferred and promoted on
    /// confirmation (see <see cref="MoveBookmarksIntoFolderAsync"/> and
    /// <see cref="DeferredCommandHelper"/>). Returns null when no root folder exists at all.
    /// </summary>
    public static async Task<BookmarkNode?> GetOrCreateFolderAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var rootFolder = await db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && n.ParentId == null && !n.IsDeleted, ct);
        if (rootFolder == null)
        {
            rootFolder = await db.BookmarkNodes
                .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && !n.IsDeleted, ct);
        }

        if (rootFolder == null)
        {
            logger.LogWarning("Could not find any root folder to place Broken Links under.");
            return null;
        }

        var brokenLinksFolder = await db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && n.ParentId == rootFolder.Id && n.Title == FolderName && !n.IsDeleted, ct);

        if (brokenLinksFolder == null)
        {
            var maxPosRoot = await db.BookmarkNodes
                .Where(n => n.ParentId == rootFolder.Id)
                .MaxAsync(n => (int?)n.Position, ct) ?? -1;

            brokenLinksFolder = new BookmarkNode
            {
                Id = Guid.NewGuid(),
                ParentId = rootFolder.Id,
                Type = NodeType.Folder,
                Title = FolderName,
                Position = maxPosRoot + 1,
                SyncState = SyncState.Pending,
                Version = 1,
                UpdatedAt = DateTime.UtcNow
            };
            db.BookmarkNodes.Add(brokenLinksFolder);

            // Shape must match the extension adapter's CreatePayload
            // (parentBrowserNodeId, not parentId).
            var createPayload = new
            {
                type = "Folder",
                parentBrowserNodeId = rootFolder.BrowserNodeId ?? "1",
                title = FolderName,
                url = (string?)null,
                position = brokenLinksFolder.Position
            };
            db.ExtensionCommands.Add(new ExtensionCommandEntry
            {
                Id = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                CommandType = "Create",
                BookmarkId = brokenLinksFolder.Id,
                BrowserNodeId = null,
                ExpectedVersion = 0,
                PayloadJson = JsonSerializer.Serialize(createPayload),
                CreatedAt = DateTime.UtcNow,
                Status = DeferredCommandHelper.InitialStatus(rootFolder)
            });

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Created new Broken Links folder in database, waiting for extension sync.");
        }

        return brokenLinksFolder;
    }

    /// <summary>
    /// Moves the given bookmarks into <paramref name="folder"/>. When the folder's
    /// <see cref="BookmarkNode.BrowserNodeId"/> is not yet known, the projection is still
    /// updated but the move commands are enqueued Deferred — they are promoted to Pending
    /// when the extension confirms the folder (see <see cref="DeferredCommandHelper"/>),
    /// so they never race the folder-create in the extension command loop.
    /// Saves and broadcasts once when any bookmarks were actually moved.
    /// </summary>
    public static async Task<int> MoveBookmarksIntoFolderAsync(
        AppDbContext db,
        BookmarkNode folder,
        IReadOnlyCollection<Guid> bookmarkIds,
        ILogger logger,
        CancellationToken ct)
    {
        if (bookmarkIds.Count == 0)
        {
            return 0;
        }

        var bookmarksToMove = await db.BookmarkNodes
            .Where(n => bookmarkIds.Contains(n.Id) && n.ParentId != folder.Id)
            .ToListAsync(ct);

        if (bookmarksToMove.Count == 0)
        {
            return 0;
        }

        var maxPosBroken = await db.BookmarkNodes
            .Where(n => n.ParentId == folder.Id)
            .MaxAsync(n => (int?)n.Position, ct) ?? -1;

        var commandStatus = DeferredCommandHelper.InitialStatus(folder);
        if (commandStatus == DeferredCommandHelper.DeferredStatus)
        {
            logger.LogInformation(
                "Broken Links folder has no BrowserNodeId yet; enqueuing {Count} moves as Deferred.",
                bookmarksToMove.Count);
        }

        int nextPos = maxPosBroken + 1;
        foreach (var bm in bookmarksToMove)
        {
            bm.ParentId = folder.Id;
            bm.Position = nextPos++;
            bm.SyncState = SyncState.Pending;
            bm.UpdatedAt = DateTime.UtcNow;

            var movePayload = new
            {
                parentBrowserNodeId = folder.BrowserNodeId,
                position = bm.Position
            };

            db.ExtensionCommands.Add(new ExtensionCommandEntry
            {
                Id = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                CommandType = "Move",
                BookmarkId = bm.Id,
                BrowserNodeId = bm.BrowserNodeId,
                ExpectedVersion = bm.Version,
                PayloadJson = JsonSerializer.Serialize(movePayload),
                CreatedAt = DateTime.UtcNow,
                Status = commandStatus
            });
        }

        await db.SaveChangesAsync(ct);
        await SyncWebSocketManager.BroadcastSyncAsync();
        logger.LogInformation("Moved {Count} bookmarks to Broken Links folder.", bookmarksToMove.Count);
        return bookmarksToMove.Count;
    }
}
