using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Backup;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class HtmlBookmarkRestoreEndpointTests : IntegrationTestBase
{
    [Fact]
    public async Task RestoreHtml_ReplacesAllBrowserRoots_AndQueuesRecursiveRestoreWithRealIds()
    {
        using var client = Factory.CreateClient();

        Guid oldBarId;
        Guid oldOtherId;
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var bar = Root("1", "Bookmarks bar");
            var other = Root("2", "Other bookmarks");
            var oldBar = Bookmark(bar.Id, "old-bar", "Old bar", "https://old.example/bar");
            var oldOther = Bookmark(other.Id, "old-other", "Old other", "https://old.example/other");
            oldBarId = oldBar.Id;
            oldOtherId = oldOther.Id;

            db.BookmarkNodes.AddRange(bar, other, oldBar, oldOther);
            await db.SaveChangesAsync();
        }

        const string html = """
            <!DOCTYPE NETSCAPE-Bookmark-file-1>
            <DL><p>
                <DT><H3 PERSONAL_TOOLBAR_FOLDER="true">Bookmarks bar</H3>
                <DL><p>
                    <DT><H3>Restored folder</H3>
                    <DL><p><DT><A HREF="https://example.com/restored">Restored bookmark</A></DL><p>
                </DL><p>
            </DL><p>
            """;

        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(html));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        form.Add(file, "file", "bookmarks.html");
        form.Add(new StringContent("RESTORE"), "confirm");

        using var response = await client.PostAsync("/api/backups/restore-html", form);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<HtmlBookmarkRestoreResultDto>();
        Assert.NotNull(result);
        Assert.Equal(1, result!.RestoredBookmarkCount);
        Assert.Equal(1, result.RestoredFolderCount);
        Assert.Equal(2, result.ReplacedNodeCount);
        Assert.NotEqual(Guid.Empty, result.SafetyBackupId);

        await using var verifyScope = Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.True((await verifyDb.BookmarkNodes.FindAsync(oldBarId))!.IsDeleted);
        Assert.True((await verifyDb.BookmarkNodes.FindAsync(oldOtherId))!.IsDeleted);

        var restoredFolder = await verifyDb.BookmarkNodes.SingleAsync(node => !node.IsDeleted && node.Title == "Restored folder");
        var restoredBookmark = await verifyDb.BookmarkNodes.SingleAsync(node => !node.IsDeleted && node.Title == "Restored bookmark");
        Assert.Equal(restoredFolder.Id, restoredBookmark.ParentId);

        var restoreCommand = await verifyDb.ExtensionCommands.SingleAsync(command =>
            command.CommandType == "Restore" && command.BookmarkId == restoredFolder.Id);
        using var payload = JsonDocument.Parse(restoreCommand.PayloadJson!);
        var childBookmarkId = payload.RootElement.GetProperty("children")[0].GetProperty("bookmarkId").GetGuid();
        Assert.Equal(restoredBookmark.Id, childBookmarkId);
        Assert.NotEqual(Guid.Empty, childBookmarkId);

        var deleteCommands = await verifyDb.ExtensionCommands.Where(command => command.CommandType == "Delete").ToListAsync();
        Assert.Contains(deleteCommands, command => command.BookmarkId == oldBarId);
        Assert.Contains(deleteCommands, command => command.BookmarkId == oldOtherId);

        var safety = await verifyDb.BackupManifests.FindAsync(result.SafetyBackupId);
        Assert.NotNull(safety);
        Assert.Equal(BackupManifestTrigger.PreRestore, safety!.Trigger);
        Assert.Equal(BackupManifestStatus.Succeeded, safety.Status);
    }

    [Fact]
    public async Task RestoreHtml_RequiresExactConfirmation()
    {
        using var client = Factory.CreateClient();
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(Encoding.UTF8.GetBytes(
            "<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><p></DL><p>"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        form.Add(file, "file", "bookmarks.html");
        form.Add(new StringContent("restore"), "confirm");

        using var response = await client.PostAsync("/api/backups/restore-html", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static BookmarkNode Root(string browserNodeId, string title) => new()
    {
        Id = Guid.NewGuid(),
        Type = NodeType.Folder,
        Title = title,
        Position = int.Parse(browserNodeId) - 1,
        IsProtected = true,
        SyncState = SyncState.Synced,
        Version = 1,
        UpdatedAt = DateTime.UtcNow,
        BrowserNodeId = browserNodeId,
        ParentBrowserNodeId = "0"
    };

    private static BookmarkNode Bookmark(Guid parentId, string browserNodeId, string title, string url) => new()
    {
        Id = Guid.NewGuid(),
        ParentId = parentId,
        Type = NodeType.Bookmark,
        Title = title,
        Url = url,
        Position = 0,
        SyncState = SyncState.Synced,
        Version = 1,
        UpdatedAt = DateTime.UtcNow,
        BrowserNodeId = browserNodeId
    };
}
