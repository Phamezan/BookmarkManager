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
    /// if missing. When newly created, saves and returns immediately with a null
    /// <see cref="BookmarkNode.BrowserNodeId"/> — callers must not move bookmarks into it until
    /// the extension reports the real id back (see <see cref="MoveBookmarksIntoFolderAsync"/>).
    /// Returns null when no root folder exists at all.
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

            var createPayload = new
            {
                parentId = rootFolder.BrowserNodeId ?? "1",
                title = FolderName
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
                Status = "Pending"
            });

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Created new Broken Links folder in database, waiting for extension sync.");
        }

        return brokenLinksFolder;
    }

    /// <summary>
    /// Moves the given bookmarks into <paramref name="folder"/>, deferring entirely (no-op,
    /// returns 0) when the folder's <see cref="BookmarkNode.BrowserNodeId"/> is not yet known —
    /// moving bookmarks before Brave confirms the folder exists would race the extension command
    /// loop. Saves and broadcasts once when any bookmarks were actually moved.
    /// </summary>
    public static async Task<int> MoveBookmarksIntoFolderAsync(
        AppDbContext db,
        BookmarkNode folder,
        IReadOnlyCollection<Guid> bookmarkIds,
        ILogger logger,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(folder.BrowserNodeId))
        {
            logger.LogWarning("Broken Links folder has no BrowserNodeId yet. Deferring bookmark movements until next scan/sync.");
            return 0;
        }

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
                Status = "Pending"
            });
        }

        await db.SaveChangesAsync(ct);
        await SyncWebSocketManager.BroadcastSyncAsync();
        logger.LogInformation("Moved {Count} bookmarks to Broken Links folder.", bookmarksToMove.Count);
        return bookmarksToMove.Count;
    }
}
