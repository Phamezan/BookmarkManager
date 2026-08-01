using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookmarkManager.Api.IntegrationTests;

/// <summary>
/// Domain auto-folder: bookmarks created via extension events are filed into the
/// folder matching their classified domain (e.g. a novelfire link saved into
/// "Manga" is moved to the novel folder), with a Move command for the extension.
/// </summary>
public sealed class ExtensionDomainFolderMoveTests : IntegrationTestBase
{
    private AppDbContext GetDb(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<AppDbContext>();

    private async Task<(Guid MangaId, Guid NovelId)> SeedFoldersAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = GetDb(scope);
        var manga = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Type = NodeType.Folder,
            Title = "Manga",
            BrowserNodeId = "10",
            Position = 0,
            SyncState = SyncState.Synced,
            UpdatedAt = DateTime.UtcNow
        };
        var novel = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Type = NodeType.Folder,
            Title = "Noveller",
            BrowserNodeId = "11",
            Position = 1,
            SyncState = SyncState.Synced,
            UpdatedAt = DateTime.UtcNow
        };
        db.BookmarkNodes.AddRange(manga, novel);
        await db.SaveChangesAsync();
        return (manga.Id, novel.Id);
    }

    private static EventBatchRequest CreatedBatch(string browserNodeId, string parentBrowserNodeId, string title, string url) => new()
    {
        BatchId = Guid.NewGuid(),
        ExtensionClientId = Guid.NewGuid(),
        ConfigVersion = 1,
        Events =
        [
            new ExtensionEventDto
            {
                EventId = Guid.NewGuid(),
                EventType = "Created",
                BrowserNodeId = browserNodeId,
                OccurredAt = DateTime.UtcNow,
                Payload = new
                {
                    node = new
                    {
                        browserNodeId,
                        parentBrowserNodeId,
                        type = "Bookmark",
                        title,
                        url,
                        position = 0,
                        isProtected = false
                    }
                }
            }
        ]
    };

    [Fact]
    public async Task CreatedNovelBookmark_InMangaFolder_IsMovedToNovelFolder()
    {
        using var client = Factory.CreateClient();
        var (_, novelId) = await SeedFoldersAsync();

        var batch = CreatedBatch("950", "10", "Some Series - Novel Fire", "https://novelfire.net/book/some-series");
        using var response = await client.PostAsJsonAsync("/api/extension/events", batch);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = Factory.Services.CreateScope();
        var db = GetDb(scope);
        var node = await db.BookmarkNodes.SingleAsync(n => n.BrowserNodeId == "950");
        Assert.Equal(novelId, node.ParentId);

        var moveCommand = await db.ExtensionCommands
            .SingleOrDefaultAsync(c => c.BookmarkId == node.Id && c.CommandType == "Move");
        Assert.NotNull(moveCommand);
        using var payload = JsonDocument.Parse(moveCommand.PayloadJson);
        Assert.Equal("11", payload.RootElement.GetProperty("parentBrowserNodeId").GetString());
    }

    [Fact]
    public async Task CreatedNovelBookmark_InNovelFolder_StaysPut()
    {
        using var client = Factory.CreateClient();
        var (_, novelId) = await SeedFoldersAsync();

        var batch = CreatedBatch("951", "11", "Some Series - Novel Fire", "https://novelfire.net/book/some-series");
        using var response = await client.PostAsJsonAsync("/api/extension/events", batch);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = Factory.Services.CreateScope();
        var db = GetDb(scope);
        var node = await db.BookmarkNodes.SingleAsync(n => n.BrowserNodeId == "951");
        Assert.Equal(novelId, node.ParentId);
        Assert.Equal(0, await db.ExtensionCommands.CountAsync(c => c.BookmarkId == node.Id && c.CommandType == "Move"));
    }

    [Fact]
    public async Task CreatedGeneralBookmark_InMangaFolder_StaysPut()
    {
        using var client = Factory.CreateClient();
        var (mangaId, _) = await SeedFoldersAsync();

        // github.com classifies as General — no media domain, no auto-move.
        var batch = CreatedBatch("952", "10", "aspnetcore", "https://github.com/dotnet/aspnetcore");
        using var response = await client.PostAsJsonAsync("/api/extension/events", batch);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = Factory.Services.CreateScope();
        var db = GetDb(scope);
        var node = await db.BookmarkNodes.SingleAsync(n => n.BrowserNodeId == "952");
        Assert.Equal(mangaId, node.ParentId);
        Assert.Equal(0, await db.ExtensionCommands.CountAsync(c => c.BookmarkId == node.Id && c.CommandType == "Move"));
    }

    [Fact]
    public async Task CreatedAnimeBookmark_IsMovedToContentDominantFolder()
    {
        using var client = Factory.CreateClient();
        await SeedFoldersAsync();

        // A neutrally-named folder full of anime URLs is the content-evidence
        // winner for anime, even though no folder title says "anime".
        Guid tftId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            var tft = new BookmarkNode
            {
                Id = Guid.NewGuid(),
                Type = NodeType.Folder,
                Title = "TFT",
                BrowserNodeId = "12",
                Position = 2,
                SyncState = SyncState.Synced,
                UpdatedAt = DateTime.UtcNow
            };
            db.BookmarkNodes.Add(tft);
            for (var i = 0; i < 5; i++)
            {
                db.BookmarkNodes.Add(new BookmarkNode
                {
                    Id = Guid.NewGuid(),
                    ParentId = tft.Id,
                    Type = NodeType.Bookmark,
                    Title = $"Some Anime - Episode {i}",
                    Url = $"https://www.miruro.to/watch/100{i}/some-anime",
                    BrowserNodeId = $"9{i}",
                    Position = i,
                    SyncState = SyncState.Synced,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync();
            tftId = tft.Id;
        }

        var batch = CreatedBatch("953", "10", "Gabriel DropOut - Episode 1", "https://www.miruro.to/watch/21878/gabriel-dropout?ep=1");
        using var response = await client.PostAsJsonAsync("/api/extension/events", batch);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var verifyScope = Factory.Services.CreateScope();
        var verifyDb = GetDb(verifyScope);
        var node = await verifyDb.BookmarkNodes.SingleAsync(n => n.BrowserNodeId == "953");
        Assert.Equal(tftId, node.ParentId);

        var moveCommand = await verifyDb.ExtensionCommands
            .SingleOrDefaultAsync(c => c.BookmarkId == node.Id && c.CommandType == "Move");
        Assert.NotNull(moveCommand);
        using var payload = JsonDocument.Parse(moveCommand.PayloadJson);
        Assert.Equal("12", payload.RootElement.GetProperty("parentBrowserNodeId").GetString());
    }
}
