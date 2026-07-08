using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Data;

public static class FolderHierarchy
{
    public static async Task<List<Guid>> GetDescendantFolderIdsAsync(AppDbContext db, Guid parentId, CancellationToken ct)
    {
        // Load all active folders' hierarchy in a single query to avoid N+1 database queries
        var folders = await db.BookmarkNodes
            .Where(n => n.Type == NodeType.Folder && !n.IsDeleted)
            .Select(n => new { n.Id, n.ParentId })
            .ToListAsync(ct);

        // Group by parent ID for fast lookup
        var lookup = folders
            .Where(f => f.ParentId.HasValue)
            .GroupBy(f => f.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Id).ToList());

        var result = new List<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(parentId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            if (lookup.TryGetValue(currentId, out var children))
            {
                foreach (var child in children)
                {
                    result.Add(child);
                    queue.Enqueue(child);
                }
            }
        }

        return result;
    }

    public static async Task<string?> BuildFolderPathAsync(AppDbContext db, Guid? folderId, CancellationToken ct)
    {
        if (!folderId.HasValue)
            return null;

        var titles = new Stack<string>();
        var currentId = folderId;
        for (var depth = 0; currentId.HasValue && depth < 32; depth++)
        {
            var folder = await db.BookmarkNodes
                .AsNoTracking()
                .Where(n => n.Id == currentId.Value && n.Type == NodeType.Folder && !n.IsDeleted)
                .Select(n => new { n.Title, n.ParentId })
                .FirstOrDefaultAsync(ct);

            if (folder is null)
                break;

            titles.Push(folder.Title);
            currentId = folder.ParentId;
        }

        return titles.Count == 0 ? null : string.Join(" / ", titles);
    }

    public static async Task MarkDeletedRecursiveAsync(AppDbContext db, Guid folderId, DateTime deletedAt, DateTime purgeAfter, CancellationToken ct)
    {
        var children = await db.BookmarkNodes
            .Where(n => n.ParentId == folderId && !n.IsDeleted)
            .ToListAsync(ct);

        foreach (var child in children)
        {
            child.IsDeleted = true;
            child.DeletedAt = deletedAt;
            child.PurgeAfter = purgeAfter;
            child.SyncState = SyncState.Pending;

            if (child.Type == NodeType.Folder)
            {
                await MarkDeletedRecursiveAsync(db, child.Id, deletedAt, purgeAfter, ct);
            }
        }
    }
}
