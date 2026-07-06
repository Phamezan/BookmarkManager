using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

public partial class BookmarksController
{
    private Task<List<Guid>> GetDescendantFolderIdsAsync(Guid parentId, CancellationToken ct)
        => FolderHierarchy.GetDescendantFolderIdsAsync(_db, parentId, ct);



    private async Task MarkDeletedRecursiveAsync(Guid folderId, DateTime deletedAt, DateTime purgeAfter, CancellationToken ct)
    {
    var children = await _db.BookmarkNodes
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
            await MarkDeletedRecursiveAsync(child.Id, deletedAt, purgeAfter, ct);
        }
    }
    }

    private async Task<string?> BuildFolderPathAsync(Guid? folderId, CancellationToken ct)
    {
    if (!folderId.HasValue)
        return null;

    var titles = new Stack<string>();
    var currentId = folderId;
    for (var depth = 0; currentId.HasValue && depth < 32; depth++)
    {
        var folder = await _db.BookmarkNodes
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

}
