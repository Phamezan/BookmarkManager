using System.Net;
using System.Net.Http.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookmarkManager.Api.IntegrationTests;

/// <summary>
/// Snapshot imports carry no server-side metadata; series-root bookmarks must still
/// get the PlanToRead status on import (same heuristic as the creation paths) so the
/// "Saved for later" shelf survives a re-import.
/// </summary>
public sealed class ExtensionSnapshotTests : IntegrationTestBase
{
    [Fact]
    public async Task SnapshotImport_AppliesPlanToReadToSeriesRootBookmarksOnly()
    {
        using var client = Factory.CreateClient();
        var payload = new SnapshotRequestPayloadDto
        {
            RequestId = Guid.NewGuid(),
            ConfigVersion = 1,
            CapturedAt = DateTime.UtcNow,
            Roots =
            [
                new SnapshotRootPayloadDto
                {
                    Root = new BookmarkNodeDto
                    {
                        Type = NodeType.Folder,
                        Title = "Bookmarks bar",
                        BrowserNodeId = "1",
                        Children =
                        [
                            new BookmarkNodeDto
                            {
                                Type = NodeType.Bookmark,
                                Title = "Some Series",
                                Url = "https://novelfire.net/book/some-series",
                                BrowserNodeId = "100",
                                ParentBrowserNodeId = "1"
                            },
                            new BookmarkNodeDto
                            {
                                Type = NodeType.Bookmark,
                                Title = "Some Series - Chapter 5",
                                Url = "https://novelfire.net/book/some-series/chapter-5",
                                BrowserNodeId = "101",
                                ParentBrowserNodeId = "1"
                            }
                        ]
                    }
                }
            ]
        };

        using var response = await client.PostAsJsonAsync("/api/extension/snapshot", payload);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seriesRoot = await db.BookmarkNodes.SingleAsync(n => n.BrowserNodeId == "100");
        var chapter = await db.BookmarkNodes.SingleAsync(n => n.BrowserNodeId == "101");
        Assert.Equal(BookmarkReadingStatus.PlanToRead, seriesRoot.Status);
        Assert.Null(chapter.Status);
    }

    [Fact]
    public async Task SnapshotReimport_BackfillsPlanToReadButPreservesExplicitStatus()
    {
        using var client = Factory.CreateClient();

        var payload = new SnapshotRequestPayloadDto
        {
            RequestId = Guid.NewGuid(),
            ConfigVersion = 1,
            CapturedAt = DateTime.UtcNow,
            Roots =
            [
                new SnapshotRootPayloadDto
                {
                    Root = new BookmarkNodeDto
                    {
                        Type = NodeType.Folder,
                        Title = "Bookmarks bar",
                        BrowserNodeId = "1",
                        Children =
                        [
                            new BookmarkNodeDto
                            {
                                Type = NodeType.Bookmark,
                                Title = "Some Series",
                                Url = "https://novelfire.net/book/some-series",
                                BrowserNodeId = "200",
                                ParentBrowserNodeId = "1"
                            },
                            new BookmarkNodeDto
                            {
                                Type = NodeType.Bookmark,
                                Title = "Another Series",
                                Url = "https://novelfire.net/book/another-series",
                                BrowserNodeId = "201",
                                ParentBrowserNodeId = "1"
                            }
                        ]
                    }
                }
            ]
        };

        // First import creates the nodes; simulate a pre-fix import by clearing one
        // status and setting an explicit one on the other.
        using (var first = await client.PostAsJsonAsync("/api/extension/snapshot", payload))
        {
            Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var seriesRoot = await db.BookmarkNodes.SingleAsync(n => n.BrowserNodeId == "200");
            var explicitStatus = await db.BookmarkNodes.SingleAsync(n => n.BrowserNodeId == "201");
            seriesRoot.Status = null;
            explicitStatus.Status = BookmarkReadingStatus.Reading;
            await db.SaveChangesAsync();
        }

        // Reimport (new RequestId so it isn't deduped): the cleared status is
        // backfilled via the update branch, the explicit one is preserved.
        payload.RequestId = Guid.NewGuid();
        using (var second = await client.PostAsJsonAsync("/api/extension/snapshot", payload))
        {
            Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        }

        using var verifyScope = Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seriesRootNode = await verifyDb.BookmarkNodes.SingleAsync(n => n.BrowserNodeId == "200");
        var explicitStatusNode = await verifyDb.BookmarkNodes.SingleAsync(n => n.BrowserNodeId == "201");
        Assert.Equal(BookmarkReadingStatus.PlanToRead, seriesRootNode.Status);
        Assert.Equal(BookmarkReadingStatus.Reading, explicitStatusNode.Status);
    }
}
