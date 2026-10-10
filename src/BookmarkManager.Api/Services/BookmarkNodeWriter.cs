using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services;

/// <summary>
/// The write paths shared by every bookmark creator/deleter so background producers (the Suwayomi
/// library sync) cannot drift from the API: a new node always lands at the tail of its parent with
/// <see cref="SyncState.Pending"/> plus a matching "Create" command, and a soft delete always stamps
/// the 30-day purge window plus a "Delete" command. Callers own the unit of work
/// (SaveChanges + broadcast) so a batch stays one transaction.
/// </summary>
public static class BookmarkNodeWriter
{
    /// <summary>Matches <c>BookmarkNode.Title</c> EF max length in AppDbContext.</summary>
    public const int MaxTitleLength = 500;

    public static string ClampTitle(string title)
    {
        var trimmed = title.Trim();
        return trimmed.Length <= MaxTitleLength
            ? trimmed
            : trimmed[..MaxTitleLength];
    }

    /// <summary>
    /// Appends <paramref name="node"/> as the last child of its already-set <see cref="BookmarkNode.ParentId"/>
    /// and enqueues its "Create" extension command (deferred when <paramref name="targetFolder"/> has no
    /// confirmed browser id). Does not save.
    /// </summary>
    public static async Task AppendNewBookmarkAsync(
        AppDbContext db,
        BookmarkNode node,
        BookmarkNode? targetFolder,
        CancellationToken ct)
    {
        var maxPos = node.ParentId is { } parentId
            ? await db.BookmarkNodes.Where(n => n.ParentId == parentId).MaxAsync(n => (int?)n.Position, ct) ?? -1
            : await db.BookmarkNodes.Where(n => n.ParentId == null).MaxAsync(n => (int?)n.Position, ct) ?? -1;

        var now = DateTime.UtcNow;
        node.Position = maxPos + 1;
        node.SyncState = SyncState.Pending;
        if (node.Version <= 0)
        {
            node.Version = 1;
        }

        if (node.CreatedAt == default)
        {
            node.CreatedAt = now;
        }

        if (node.UpdatedAt == default)
        {
            node.UpdatedAt = now;
        }

        db.BookmarkNodes.Add(node);

        var payload = new
        {
            type = node.Type == NodeType.Folder ? "Folder" : "Bookmark",
            parentBrowserNodeId = targetFolder?.BrowserNodeId ?? "0",
            title = node.Title,
            url = node.Url,
            position = node.Position
        };

        db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Create",
            BookmarkId = node.Id,
            BrowserNodeId = null,
            ExpectedVersion = node.Version,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
            CreatedAt = now,
            Status = DeferredCommandHelper.InitialStatus(targetFolder)
        });
    }

    /// <summary>
    /// Bumps <paramref name="node"/>'s version, marks it Pending and enqueues its "Update" extension
    /// command for the current title/url. Callers must have already applied the change. Does not save.
    /// </summary>
    public static void EnqueueUpdateCommand(AppDbContext db, BookmarkNode node)
    {
        node.Version++;
        node.SyncState = SyncState.Pending;
        node.UpdatedAt = DateTime.UtcNow;

        var payload = new
        {
            title = node.Title,
            url = node.Url
        };

        db.ExtensionCommands.Add(new ExtensionCommandEntry
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

    /// <summary>
    /// Marks <paramref name="node"/> (and, for a folder, its subtree) soft-deleted with the standard
    /// 30-day purge window and enqueues its "Delete" extension command. Does not save.
    /// </summary>
    public static async Task ApplySoftDeleteAsync(AppDbContext db, BookmarkNode node, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        node.IsDeleted = true;
        node.DeletedAt = now;
        node.PurgeAfter = now.AddDays(30);
        node.SyncState = SyncState.Pending;

        if (node.Type == NodeType.Folder)
        {
            await FolderHierarchy.MarkDeletedRecursiveAsync(db, node.Id, node.DeletedAt.Value, node.PurgeAfter.Value, ct);
        }

        var payload = new
        {
            recursive = true
        };

        db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Delete",
            BookmarkId = node.Id,
            BrowserNodeId = node.BrowserNodeId,
            ExpectedVersion = node.Version,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
            CreatedAt = now,
            Status = "Pending"
        });
    }
}
