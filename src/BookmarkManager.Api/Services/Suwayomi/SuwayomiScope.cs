using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>Folder-subtree and import-preview queries shared by the controller and the run job.</summary>
public static class SuwayomiScope
{
    public static async Task<(string Title, List<Guid> FolderIds)?> ResolveFolderAsync(
        AppDbContext db, Guid folderId, CancellationToken ct)
    {
        var folders = await db.BookmarkNodes
            .Where(n => n.Type == NodeType.Folder && !n.IsDeleted)
            .Select(n => new { n.Id, n.Title, n.ParentId })
            .ToListAsync(ct).ConfigureAwait(false);

        var root = folders.FirstOrDefault(f => f.Id == folderId);
        if (root is null)
        {
            return null;
        }

        var byParent = folders
            .Where(f => f.ParentId != null)
            .GroupBy(f => f.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Id).ToList());

        var ids = new List<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(folderId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            ids.Add(id);
            if (byParent.TryGetValue(id, out var children))
            {
                foreach (var child in children)
                {
                    stack.Push(child);
                }
            }
        }

        return (root.Title, ids);
    }

    public static IQueryable<BookmarkNode> BookmarksInFolder(AppDbContext db, List<Guid> folderIds)
        => db.BookmarkNodes.Where(n =>
            n.Type == NodeType.Bookmark &&
            !n.IsDeleted &&
            n.Url != null &&
            n.ParentId != null &&
            folderIds.Contains(n.ParentId.Value));

    public static async Task<SuwayomiImportPreviewDto?> GetPreviewAsync(AppDbContext db, Guid folderId, CancellationToken ct)
    {
        var resolved = await ResolveFolderAsync(db, folderId, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        var (title, folderIds) = resolved.Value;
        var bookmarks = BookmarksInFolder(db, folderIds);

        var total = await bookmarks.CountAsync(ct).ConfigureAwait(false);
        var alreadyLinked = await bookmarks.CountAsync(n => n.SuwayomiMangaId != null, ct).ConfigureAwait(false);
        var bookmarkIds = await bookmarks.Select(n => n.Id).ToListAsync(ct).ConfigureAwait(false);

        var pendingBookmarkIds = await db.UrlMigrationProposals
            .Where(p => p.IsSuwayomi
                && (p.Status == "Pending" || p.Status == "Approved")
                && bookmarkIds.Contains(p.BookmarkId))
            .Select(p => p.BookmarkId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        // A linked bookmark already has a Suwayomi id, so don't double-subtract it from the
        // pending bucket.
        var linked = await bookmarks.Where(n => n.SuwayomiMangaId != null).Select(n => n.Id).ToListAsync(ct).ConfigureAwait(false);
        var linkedSet = linked.ToHashSet();
        var pendingOnly = pendingBookmarkIds.Count(id => !linkedSet.Contains(id));
        var toImport = Math.Max(0, total - alreadyLinked - pendingOnly);

        return new SuwayomiImportPreviewDto
        {
            FolderTitle = title,
            Total = total,
            AlreadyLinked = alreadyLinked,
            PendingReview = pendingBookmarkIds.Count,
            ToImport = toImport
        };
    }
}
