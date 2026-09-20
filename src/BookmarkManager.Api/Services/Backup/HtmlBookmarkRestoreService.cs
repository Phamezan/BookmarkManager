using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.Backup;

public sealed class HtmlBookmarkRestoreService(AppDbContext db, IBackupService backupService)
{
    private static readonly HashSet<string> ReplaceableBrowserRootIds = ["1", "2", "3"];

    private sealed record PreparedImport(
        BookmarkNode Node,
        ImportedBookmarkNode Imported,
        List<PreparedImport> Children);

    public async Task<HtmlBookmarkRestoreResultDto> RestoreAsync(
        Stream htmlStream,
        string confirm,
        CancellationToken ct)
    {
        if (!string.Equals(confirm, "RESTORE", StringComparison.Ordinal))
            throw new BackupInvalidConfirmException("Type RESTORE exactly to replace the current bookmark tree.");

        using var reader = new StreamReader(htmlStream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var html = await reader.ReadToEndAsync(ct);
        var importedRoots = NetscapeBookmarkHtmlParser.Parse(html);

        var safety = await backupService.CreateBackupAsync(BackupManifestTrigger.PreRestore, ct);
        if (safety.Status != BackupManifestStatus.Succeeded)
            throw new BackupRestoreException("Safety backup failed; HTML restore was aborted.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var now = DateTime.UtcNow;

            // IsProtected is inherited by descendants in the browser snapshot mapper, so
            // identify Chromium's actual fixed roots by their well-known browser IDs.
            var browserRoots = await db.BookmarkNodes
                .Where(n => !n.IsDeleted &&
                            n.BrowserNodeId != null &&
                            ReplaceableBrowserRootIds.Contains(n.BrowserNodeId))
                .ToDictionaryAsync(n => n.BrowserNodeId!, ct);

            foreach (var requiredId in importedRoots.Select(root => root.BrowserRootId).Distinct())
            {
                if (!browserRoots.ContainsKey(requiredId))
                {
                    throw new InvalidOperationException(
                        $"Browser root {requiredId} is not synchronized yet. Run a manual sync and try again.");
                }
            }

            // This is a replacement restore, not an additive import. Clear every
            // replaceable browser root that exists locally even when the HTML export
            // omits that root because it was empty at backup time.
            var rootDbIds = browserRoots.Values.Select(root => root.Id).ToHashSet();
            var oldTopLevel = await db.BookmarkNodes
                .Where(n => !n.IsDeleted && n.ParentId != null && rootDbIds.Contains(n.ParentId.Value))
                .OrderBy(n => n.Position)
                .ToListAsync(ct);

            var replacedNodeCount = 0;
            foreach (var old in oldTopLevel)
            {
                replacedNodeCount += await MarkDeletedRecursiveAsync(old, now, ct);

                if (!string.IsNullOrWhiteSpace(old.BrowserNodeId))
                {
                    db.ExtensionCommands.Add(new ExtensionCommandEntry
                    {
                        Id = Guid.NewGuid(),
                        OperationId = Guid.NewGuid(),
                        CommandType = "Delete",
                        BookmarkId = old.Id,
                        BrowserNodeId = old.BrowserNodeId,
                        ExpectedVersion = old.Version,
                        PayloadJson = JsonSerializer.Serialize(new { recursive = true }),
                        CreatedAt = now,
                        Status = "Pending"
                    });
                }
            }

            var restoredBookmarks = 0;
            var restoredFolders = 0;
            var restoreCreatedAt = now.AddMilliseconds(1);

            foreach (var importedRoot in importedRoots)
            {
                var dbRoot = browserRoots[importedRoot.BrowserRootId];

                for (var i = 0; i < importedRoot.Children.Count; i++)
                {
                    var prepared = AddImportedTree(
                        importedRoot.Children[i],
                        dbRoot.Id,
                        importedRoot.BrowserRootId,
                        i,
                        ref restoredBookmarks,
                        ref restoredFolders);

                    db.ExtensionCommands.Add(new ExtensionCommandEntry
                    {
                        Id = Guid.NewGuid(),
                        OperationId = Guid.NewGuid(),
                        CommandType = "Restore",
                        BookmarkId = prepared.Node.Id,
                        BrowserNodeId = null,
                        ExpectedVersion = prepared.Node.Version,
                        PayloadJson = JsonSerializer.Serialize(
                            BuildRestorePayload(prepared, importedRoot.BrowserRootId)),
                        CreatedAt = restoreCreatedAt,
                        Status = "Pending"
                    });
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();

            return new HtmlBookmarkRestoreResultDto
            {
                SafetyBackupId = safety.Id,
                RestoredBookmarkCount = restoredBookmarks,
                RestoredFolderCount = restoredFolders,
                ReplacedNodeCount = replacedNodeCount,
                Message = $"Queued restore of {restoredBookmarks:N0} bookmarks and {restoredFolders:N0} folders."
            };
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<int> MarkDeletedRecursiveAsync(
        BookmarkNode node,
        DateTime now,
        CancellationToken ct)
    {
        var count = 1;
        node.IsDeleted = true;
        node.DeletedAt = now;
        node.PurgeAfter = now.AddDays(30);
        node.SyncState = SyncState.Pending;
        node.UpdatedAt = now;

        if (node.Type == NodeType.Folder)
        {
            var children = await db.BookmarkNodes
                .Where(n => !n.IsDeleted && n.ParentId == node.Id)
                .ToListAsync(ct);

            foreach (var child in children)
                count += await MarkDeletedRecursiveAsync(child, now, ct);
        }

        return count;
    }

    private PreparedImport AddImportedTree(
        ImportedBookmarkNode imported,
        Guid parentId,
        string? parentBrowserNodeId,
        int position,
        ref int bookmarkCount,
        ref int folderCount)
    {
        var node = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Type = imported.IsFolder ? NodeType.Folder : NodeType.Bookmark,
            Title = imported.Title,
            Url = imported.Url,
            Position = position,
            SyncState = SyncState.Pending,
            Version = 1,
            UpdatedAt = DateTime.UtcNow,
            ParentBrowserNodeId = parentBrowserNodeId
        };
        db.BookmarkNodes.Add(node);

        if (imported.IsFolder)
            folderCount++;
        else
            bookmarkCount++;

        var children = new List<PreparedImport>(imported.Children.Count);
        for (var i = 0; i < imported.Children.Count; i++)
        {
            children.Add(AddImportedTree(
                imported.Children[i],
                node.Id,
                null,
                i,
                ref bookmarkCount,
                ref folderCount));
        }

        return new PreparedImport(node, imported, children);
    }

    private static object BuildRestorePayload(
        PreparedImport prepared,
        string parentBrowserNodeId)
        => new
        {
            bookmarkId = prepared.Node.Id,
            type = prepared.Imported.IsFolder ? "Folder" : "Bookmark",
            parentBrowserNodeId,
            title = prepared.Imported.Title,
            url = prepared.Imported.Url,
            position = prepared.Node.Position,
            children = prepared.Imported.IsFolder
                ? prepared.Children.Select(BuildRestoreChildPayload).ToList()
                : null
        };

    private static object BuildRestoreChildPayload(PreparedImport prepared)
        => new
        {
            bookmarkId = prepared.Node.Id,
            type = prepared.Imported.IsFolder ? "Folder" : "Bookmark",
            parentBrowserNodeId = string.Empty,
            title = prepared.Imported.Title,
            url = prepared.Imported.Url,
            position = prepared.Node.Position,
            children = prepared.Imported.IsFolder
                ? prepared.Children.Select(BuildRestoreChildPayload).ToList()
                : null
        };
}
