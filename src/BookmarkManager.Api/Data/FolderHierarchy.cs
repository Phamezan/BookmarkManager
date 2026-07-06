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
}
