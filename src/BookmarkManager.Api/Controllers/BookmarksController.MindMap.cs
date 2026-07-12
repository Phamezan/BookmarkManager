using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

public partial class BookmarksController
{
    /// <summary>
    /// Flat list of every live node (folders + bookmarks) for the Mind Map
    /// visualizer. The client reconstructs the tree from ParentId.
    /// </summary>
    [HttpGet("mindmap")]
    public async Task<List<MindMapNodeDto>> GetMindMapNodesAsync(CancellationToken ct)
    {
        return await _db.BookmarkNodes
            .Where(n => !n.IsDeleted)
            .OrderBy(n => n.Position)
            .Select(n => new MindMapNodeDto
            {
                Id = n.Id,
                ParentId = n.ParentId,
                Type = n.Type,
                Title = n.Title,
                Url = n.Url,
                Position = n.Position,
                IsFavorite = n.IsFavorite
            })
            .ToListAsync(ct);
    }
}
