using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.Backup;

public sealed class HtmlBookmarkRestoreService(AppDbContext db, IBackupService backupService)
{
    public async Task<HtmlBookmarkRestoreResultDto> RestoreAsync(Stream htmlStream, string confirm, CancellationToken ct)
    {
        if (!string.Equals(confirm, "RESTORE", StringComparison.Ordinal))
            throw new BackupInvalidConfirmException("Type RESTORE exactly to replace the current bookmark tree.");

        using var reader = new StreamReader(htmlStream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var html = await reader.ReadToEndAsync(ct);
        var importedRoots = NetscapeBookmarkHtmlParser.Parse(html);
        if (importedRoots.Sum(r => r.Children.Count) == 0)
            throw new InvalidDataException("The bookmark file does not contain any restorable bookmarks or folders.");

        var safety = await backupService.CreateBackupAsync(BackupManifestTrigger.PreRestore, ct);
        if (safety.Status != BackupManifestStatus.Succeeded)
            throw new BackupRestoreException("Safety backup failed; HTML restore was aborted.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            var protectedRoots = await db.BookmarkNodes
                .Where(n => !n.IsDeleted && n.IsProtected && n.BrowserNodeId != null)
                .ToDictionaryAsync(n => n.BrowserNodeId!, ct);

            foreach (var requiredId in importedRoots.Select(r => r.BrowserRootId).Where(id => id is not null).Distinct())
            {
                if (!protectedRoots.ContainsKey(requiredId!))
                    throw new InvalidOperationException($"Browser root {requiredId} is not synchronized yet. Run a manual sync and try again.");
            }

            var targetRootIds = importedRoots
                .Select(r => r.BrowserRootId)
                .Where(id => id is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);

            var targetDbIds = protectedRoots
                .Where(kvp => targetRootIds.Contains(kvp.Key))
                .Select(kvp => kvp.Value.Id)
                .ToHashSet();

            var oldTopLevel = await db.BookmarkNodes
                .Where(n => !n.IsDeleted && n.ParentId != null && targetDbIds.Contains(n.ParentId.Value))
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
                var browserRootId = importedRoot.BrowserRootId ?? "1";
                if (!protectedRoots.TryGetValue(browserRootId, out var dbRoot))
                    throw new InvalidOperationException($"Browser root {browserRootId} is not synchronized yet.");

                for (var i = 0; i < importedRoot.Children.Count; i++)
                {
                    var imported = importedRoot.Children[i];
                    var node = AddImportedTree(imported, dbRoot.Id, browserRootId, i, ref restoredBookmarks, ref restoredFolders);
                    db.ExtensionCommands.Add(new ExtensionCommandEntry
                    {
                        Id = Guid.NewGuid(),
                        OperationId = Guid.NewGuid(),
                        CommandType = "Restore",
                        BookmarkId = node.Id,
                        BrowserNodeId = null,
                        ExpectedVersion = node.Version,
                        PayloadJson = JsonSerializer.Serialize(BuildRestorePayload(node, imported, browserRootId)),
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

    private async Task<int> MarkDeletedRecursiveAsync(BookmarkNode node, DateTime now, CancellationToken ct)
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

    private BookmarkNode AddImportedTree(
        ImportedBookmarkNode imported,
        Guid parentId,
        string parentBrowserNodeId,
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

        if (imported.IsFolder) folderCount++;
        else bookmarkCount++;

        for (var i = 0; i < imported.Children.Count; i++)
            AddImportedTree(imported.Children[i], node.Id, string.Empty, i, ref bookmarkCount, ref folderCount);

        return node;
    }

    private static object BuildRestorePayload(BookmarkNode node, ImportedBookmarkNode imported, string parentBrowserNodeId)
        => new
        {
            bookmarkId = node.Id,
            type = imported.IsFolder ? "Folder" : "Bookmark",
            parentBrowserNodeId,
            title = imported.Title,
            url = imported.Url,
            position = node.Position,
            children = imported.IsFolder
                ? imported.Children.Select((child, index) => BuildRestoreChildPayload(child, index)).ToList()
                : null
        };

    private static object BuildRestoreChildPayload(ImportedBookmarkNode imported, int position)
        => new
        {
            bookmarkId = Guid.Empty,
            type = imported.IsFolder ? "Folder" : "Bookmark",
            parentBrowserNodeId = string.Empty,
            title = imported.Title,
            url = imported.Url,
            position,
            children = imported.IsFolder
                ? imported.Children.Select((child, index) => BuildRestoreChildPayload(child, index)).ToList()
                : null
        };
}
