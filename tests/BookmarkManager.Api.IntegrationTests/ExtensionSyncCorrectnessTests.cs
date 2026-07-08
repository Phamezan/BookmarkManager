using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BookmarkManager.Api.IntegrationTests;

/// <summary>
/// Phase 1 sync-protocol correctness: echo suppression, atomic event apply,
/// and pending-parent command deferral.
/// </summary>
public sealed class ExtensionSyncCorrectnessTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private AppDbContext GetDb(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<AppDbContext>();

    [Fact]
    public async Task EventCausedBySucceededCommandDoesNotTouchProjection()
    {
        using var client = Factory.CreateClient();
        var nodeId = Guid.NewGuid();
        var operationId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = nodeId,
                Title = "Original Title",
                Type = NodeType.Bookmark,
                Url = "https://example.com/",
                BrowserNodeId = "500",
                SyncState = SyncState.Synced,
                Version = 2,
                UpdatedAt = DateTime.UtcNow
            });
            db.ExtensionCommands.Add(new ExtensionCommandEntry
            {
                Id = Guid.NewGuid(),
                OperationId = operationId,
                CommandType = "Update",
                BookmarkId = nodeId,
                BrowserNodeId = "500",
                ExpectedVersion = 2,
                PayloadJson = """{"title":"Server Title","url":"https://example.com/"}""",
                CreatedAt = DateTime.UtcNow,
                Status = "Succeeded"
            });
            await db.SaveChangesAsync();
        }

        var batch = new EventBatchRequest
        {
            BatchId = Guid.NewGuid(),
            ExtensionClientId = Guid.NewGuid(),
            ConfigVersion = 1,
            Events =
            [
                new ExtensionEventDto
                {
                    EventId = Guid.NewGuid(),
                    EventType = "Changed",
                    BrowserNodeId = "500",
                    OccurredAt = DateTime.UtcNow,
                    CausedByOperationId = operationId,
                    Payload = new { title = "Echoed Title", url = "https://example.com/" }
                }
            ]
        };

        using var response = await client.PostAsJsonAsync("/api/extension/events", batch);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<EventBatchResponse>())!;
        Assert.Single(body.AcceptedEventIds);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            var node = await db.BookmarkNodes.SingleAsync(n => n.Id == nodeId);
            Assert.Equal("Original Title", node.Title);

            // The event row is still persisted for audit/dedup.
            Assert.Equal(1, await db.ExtensionEvents.CountAsync(e => e.CausedByOperationId == operationId));
        }
    }

    [Fact]
    public async Task EventWithoutCausedByAppliesNormally()
    {
        using var client = Factory.CreateClient();
        var nodeId = Guid.NewGuid();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            db.BookmarkNodes.Add(new BookmarkNode
            {
                Id = nodeId,
                Title = "Original Title",
                Type = NodeType.Bookmark,
                Url = "https://example.com/",
                BrowserNodeId = "500",
                SyncState = SyncState.Synced,
                Version = 2,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var batch = new EventBatchRequest
        {
            BatchId = Guid.NewGuid(),
            ExtensionClientId = Guid.NewGuid(),
            ConfigVersion = 1,
            Events =
            [
                new ExtensionEventDto
                {
                    EventId = Guid.NewGuid(),
                    EventType = "Changed",
                    BrowserNodeId = "500",
                    OccurredAt = DateTime.UtcNow,
                    CausedByOperationId = null,
                    Payload = new { title = "User Edited", url = "https://example.com/" }
                }
            ]
        };

        using var response = await client.PostAsJsonAsync("/api/extension/events", batch);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            var node = await db.BookmarkNodes.SingleAsync(n => n.Id == nodeId);
            Assert.Equal("User Edited", node.Title);
        }
    }

    [Fact]
    public async Task FailedApplyRollsBackBatchSoRetryApplies()
    {
        using var client = Factory.CreateClient();
        var batchId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        // Created event whose payload lacks the required "node" property makes
        // ApplyEventChangesAsync throw after the event rows were inserted.
        var badBatch = new EventBatchRequest
        {
            BatchId = batchId,
            ExtensionClientId = Guid.NewGuid(),
            ConfigVersion = 1,
            Events =
            [
                new ExtensionEventDto
                {
                    EventId = eventId,
                    EventType = "Created",
                    BrowserNodeId = "700",
                    OccurredAt = DateTime.UtcNow,
                    Payload = new { wrong = "shape" }
                }
            ]
        };

        using (var badResponse = await client.PostAsJsonAsync("/api/extension/events", badBatch))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, badResponse.StatusCode);
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            // The failed batch must not be recorded as accepted.
            Assert.Equal(0, await db.ExtensionEvents.CountAsync(e => e.BatchId == batchId));
        }

        // Retry of the same batch (same batchId/eventId) with a valid payload applies cleanly.
        var goodBatch = new EventBatchRequest
        {
            BatchId = batchId,
            ExtensionClientId = badBatch.ExtensionClientId,
            ConfigVersion = 1,
            Events =
            [
                new ExtensionEventDto
                {
                    EventId = eventId,
                    EventType = "Created",
                    BrowserNodeId = "700",
                    OccurredAt = DateTime.UtcNow,
                    Payload = new
                    {
                        node = new
                        {
                            browserNodeId = "700",
                            parentBrowserNodeId = (string?)null,
                            type = "Bookmark",
                            title = "Recovered",
                            url = "https://example.com/recovered",
                            position = 0,
                            isProtected = false
                        }
                    }
                }
            ]
        };

        using var goodResponse = await client.PostAsJsonAsync("/api/extension/events", goodBatch);
        Assert.Equal(HttpStatusCode.Accepted, goodResponse.StatusCode);
        var body = (await goodResponse.Content.ReadFromJsonAsync<EventBatchResponse>())!;
        Assert.Contains(eventId, body.AcceptedEventIds);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            Assert.Equal(1, await db.BookmarkNodes.CountAsync(n => n.BrowserNodeId == "700"));
        }
    }

    [Fact]
    public async Task CommandsForUnconfirmedParentAreDeferredUntilFolderCompletes()
    {
        using var client = Factory.CreateClient();

        // Folder created in the manager: no BrowserNodeId until Brave confirms.
        Guid folderId;
        using (var createFolder = await client.PostAsJsonAsync(
            $"/api/bookmarks/{Guid.Empty}",
            new CreateBookmarkRequest { Title = "New Folder", Type = NodeType.Folder }))
        {
            Assert.Equal(HttpStatusCode.OK, createFolder.StatusCode);
            folderId = (await createFolder.Content.ReadFromJsonAsync<BookmarkNodeDto>(JsonOptions))!.Id;
        }

        // Bookmark created inside the unconfirmed folder must not dispatch yet.
        Guid bookmarkId;
        using (var createBookmark = await client.PostAsJsonAsync(
            $"/api/bookmarks/{folderId}",
            new CreateBookmarkRequest
            {
                Title = "Inside",
                Url = "https://example.com/inside",
                Type = NodeType.Bookmark
            }))
        {
            Assert.Equal(HttpStatusCode.OK, createBookmark.StatusCode);
            bookmarkId = (await createBookmark.Content.ReadFromJsonAsync<BookmarkNodeDto>(JsonOptions))!.Id;
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var db = GetDb(scope);
            var bookmarkCommand = await db.ExtensionCommands.SingleAsync(c => c.BookmarkId == bookmarkId);
            Assert.Equal(DeferredCommandHelper.DeferredStatus, bookmarkCommand.Status);
        }

        // Claim: only the folder create is dispatched.
        var claim = await ClaimAsync(client);
        var folderCommand = Assert.Single(claim.Commands);
        Assert.Equal(folderId, folderCommand.BookmarkId);
        Assert.Equal("Create", folderCommand.CommandType);

        // Extension confirms the folder; the deferred bookmark command promotes.
        using (var complete = await client.PostAsJsonAsync(
            $"/api/extension/commands/{folderCommand.OperationId}/complete",
            new CompletionRequest
            {
                LeaseId = folderCommand.LeaseId,
                Status = "Succeeded",
                BrowserNodeId = "77"
            }))
        {
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        }

        var secondClaim = await ClaimAsync(client);
        var bookmarkCommandDto = Assert.Single(secondClaim.Commands);
        Assert.Equal(bookmarkId, bookmarkCommandDto.BookmarkId);

        var payload = JsonSerializer.Serialize(bookmarkCommandDto.Payload);
        using var doc = JsonDocument.Parse(payload);
        Assert.Equal("77", doc.RootElement.GetProperty("parentBrowserNodeId").GetString());
    }

    private static async Task<ClaimResponse> ClaimAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/extension/commands/claim",
            new ClaimRequest { ConfigVersion = 1, MaxCommands = 10 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClaimResponse>())!;
    }
}
