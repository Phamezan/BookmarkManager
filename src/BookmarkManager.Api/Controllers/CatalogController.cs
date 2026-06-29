using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/catalog")]
public class CatalogController(AppDbContext db) : ControllerBase
{
    [HttpGet("folders")]
    public async Task<List<FolderCatalogNodeDto>> GetFoldersAsync(CancellationToken ct)
    {
        var latestClient = await db.ExtensionClients
            .OrderByDescending(c => c.LastHeartbeatAt)
            .FirstOrDefaultAsync(ct);
        if (latestClient is null)
        {
            return [];
        }

        return await db.FolderCatalogEntries
            .Where(e => e.ExtensionClientId == latestClient.Id)
            .OrderBy(e => e.Position)
            .Select(e => new FolderCatalogNodeDto
            {
                BrowserNodeId = e.BrowserNodeId,
                ParentBrowserNodeId = e.ParentBrowserNodeId,
                Title = e.Title,
                Position = e.Position,
                IsProtected = e.IsProtected
            })
            .ToListAsync(ct);
    }
}
