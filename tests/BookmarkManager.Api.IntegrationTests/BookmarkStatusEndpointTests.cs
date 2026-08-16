using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class BookmarkStatusEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task PutStatus_ValidMediaBookmark_UpdatesStatusAndProjectsFolder()
    {
        using var client = Factory.CreateClient();

        // 1. Create root "Manga" folder
        using var folderResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Manga" });
        folderResponse.EnsureSuccessStatusCode();
        var mangaFolder = await folderResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(mangaFolder);

        // 2. Create bookmark under "Manga"
        using var bmResponse = await client.PostAsJsonAsync(
            $"/api/bookmarks/{mangaFolder!.Id}",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = "Jujutsu Kaisen", Url = "https://viz.com/shonenjump/chapters/jujutsu-kaisen" });
        bmResponse.EnsureSuccessStatusCode();
        var bookmark = await bmResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(bookmark);

        // 3. Update status to Completed
        using var putResponse = await client.PutAsJsonAsync(
            $"/api/bookmarks/{bookmark!.Id}/status",
            new UpdateBookmarkStatusRequest { Status = BookmarkReadingStatus.Completed });
        putResponse.EnsureSuccessStatusCode();
        var updated = await putResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(updated);
        Assert.Equal(BookmarkReadingStatus.Completed, updated!.Metadata?.Status);
        Assert.NotEqual(mangaFolder.Id, updated.ParentId);
    }

    [Fact]
    public async Task PutStatus_InvalidStatus_ReturnsBadRequest()
    {
        using var client = Factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            $"/api/bookmarks/{Guid.NewGuid()}/status",
            new UpdateBookmarkStatusRequest { Status = "InvalidStatusName" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BulkStatus_UpdatesItemsAndReturnsGranularSummary()
    {
        using var client = Factory.CreateClient();

        // 1. Create root "Anime" folder
        using var folderResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Anime" });
        folderResponse.EnsureSuccessStatusCode();
        var animeFolder = await folderResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(animeFolder);

        // 2. Create two bookmarks
        using var bm1Response = await client.PostAsJsonAsync(
            $"/api/bookmarks/{animeFolder!.Id}",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = "Frieren", Url = "https://crunchyroll.com/frieren" });
        var bm1 = await bm1Response.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);

        using var bm2Response = await client.PostAsJsonAsync(
            $"/api/bookmarks/{animeFolder.Id}",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = "Bocchi the Rock", Url = "https://crunchyroll.com/bocchi" });
        var bm2 = await bm2Response.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);

        // 3. Bulk update to "Plan to Read"
        using var bulkResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/bulk-status",
            new BulkUpdateBookmarkStatusRequest
            {
                BookmarkIds = [bm1!.Id, bm2!.Id, Guid.NewGuid()],
                Status = BookmarkReadingStatus.PlanToRead
            });
        bulkResponse.EnsureSuccessStatusCode();
        var bulkResult = await bulkResponse.Content.ReadFromJsonAsync<BulkUpdateBookmarkStatusResponse>(Options);
        Assert.NotNull(bulkResult);
        Assert.Equal(3, bulkResult!.TotalCount);
        Assert.Equal(2, bulkResult.ChangedCount);
        Assert.Equal(1, bulkResult.SkippedCount);
    }

    [Fact]
    public async Task GetRelatedCandidates_FindsMatchingSeries()
    {
        using var client = Factory.CreateClient();

        // 1. Create root "Anime" folder
        using var folderResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Anime" });
        folderResponse.EnsureSuccessStatusCode();
        var animeFolder = await folderResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(animeFolder);

        // 2. Create Season 1 and Season 2 bookmarks
        using var bm1Response = await client.PostAsJsonAsync(
            $"/api/bookmarks/{animeFolder!.Id}",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = "Mob Psycho 100 - Season 1", Url = "https://crunchyroll.com/mob1" });
        var bm1 = await bm1Response.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);

        using var bm2Response = await client.PostAsJsonAsync(
            $"/api/bookmarks/{animeFolder.Id}",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = "Mob Psycho 100 - Season 2", Url = "https://crunchyroll.com/mob2" });
        var bm2 = await bm2Response.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);

        // 3. Query related candidates for bm1 with target status Completed
        using var candidateResponse = await client.GetAsync(
            $"/api/bookmarks/{bm1!.Id}/related-status-candidates?targetStatus={BookmarkReadingStatus.Completed}");
        candidateResponse.EnsureSuccessStatusCode();
        var candidates = await candidateResponse.Content.ReadFromJsonAsync<RelatedSeriesPreviewResponse>(Options);
        Assert.NotNull(candidates);
        Assert.Single(candidates!.Candidates);
        Assert.Equal(bm2!.Id, candidates.Candidates[0].Id);
    }

    [Fact]
    public async Task InboundBraveMoveEvent_DoesNotRewriteBookmarkStatus()
    {
        using var client = Factory.CreateClient();

        // 1. Create root "Manga" folder
        using var folderResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Manga" });
        var mangaFolder = (await folderResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        // 2. Create bookmark with Status = "Completed"
        using var bmResponse = await client.PostAsJsonAsync(
            $"/api/bookmarks/{mangaFolder.Id}",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = "Fullmetal Alchemist", Url = "https://mangadex.org/title/fma" });
        var bookmark = (await bmResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        using var statusResponse = await client.PutAsJsonAsync(
            $"/api/bookmarks/{bookmark.Id}/status",
            new UpdateBookmarkStatusRequest { Status = BookmarkReadingStatus.Completed });
        statusResponse.EnsureSuccessStatusCode();

        // 3. Simulate inbound Brave Moved event
        var moveBatch = new EventBatchRequest
        {
            BatchId = Guid.NewGuid(),
            ExtensionClientId = Guid.NewGuid(),
            ConfigVersion = 1,
            Events =
            [
                new ExtensionEventDto
                {
                    EventId = Guid.NewGuid(),
                    EventType = "Moved",
                    BrowserNodeId = bookmark.Id.ToString(),
                    OccurredAt = DateTime.UtcNow,
                    Payload = new
                    {
                        parentBrowserNodeId = mangaFolder.Id.ToString(),
                        oldParentBrowserNodeId = "999",
                        position = 5,
                        oldPosition = 0
                    }
                }
            ]
        };

        using var eventResponse = await client.PostAsJsonAsync("/api/extension/events", moveBatch);
        Assert.Equal(HttpStatusCode.Accepted, eventResponse.StatusCode);

        // 4. Verify bookmark status is still Completed
        using var getResponse = await client.GetAsync($"/api/bookmarks/{bookmark.Id}");
        getResponse.EnsureSuccessStatusCode();
        var refreshed = await getResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(refreshed);
        Assert.Equal(BookmarkReadingStatus.Completed, refreshed!.Metadata?.Status);
    }

    [Fact]
    public async Task DeferredStatusFolderCreation_CompletionPromotesDependentMoveCommand()
    {
        using var client = Factory.CreateClient();

        // 1. Create root "Manga" folder and confirm it with browser ID "10"
        using var folderResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Manga" });
        var mangaFolder = (await folderResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        var claim1 = await ClaimAsync(client);
        var mangaFolderCmd = Assert.Single(claim1.Commands);
        using var complete1 = await client.PostAsJsonAsync(
            $"/api/extension/commands/{mangaFolderCmd.OperationId}/complete",
            new CompletionRequest { LeaseId = mangaFolderCmd.LeaseId, Status = "Succeeded", BrowserNodeId = "10" });
        Assert.Equal(HttpStatusCode.OK, complete1.StatusCode);

        // 2. Create bookmark inside Manga folder as Ongoing and confirm it with browser ID "101"
        using var bmResponse = await client.PostAsJsonAsync(
            $"/api/bookmarks/{mangaFolder.Id}",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = "Dandadan", Url = "https://mangadex.org/chapter/dandadan-50", Status = BookmarkReadingStatus.Ongoing });
        var bookmark = (await bmResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        var claim2 = await ClaimAsync(client);
        var bmCreateCmd = Assert.Single(claim2.Commands);
        using var complete2 = await client.PostAsJsonAsync(
            $"/api/extension/commands/{bmCreateCmd.OperationId}/complete",
            new CompletionRequest { LeaseId = bmCreateCmd.LeaseId, Status = "Succeeded", BrowserNodeId = "101" });
        Assert.Equal(HttpStatusCode.OK, complete2.StatusCode);

        // 3. Update status to "Completed" -> creates status folder Create command AND deferred Move command
        using var statusResponse = await client.PutAsJsonAsync(
            $"/api/bookmarks/{bookmark.Id}/status",
            new UpdateBookmarkStatusRequest { Status = BookmarkReadingStatus.Completed });
        statusResponse.EnsureSuccessStatusCode();

        // 4. Claim commands -> status folder create command is ready, dependent move is deferred
        var claim3 = await ClaimAsync(client);
        var folderCreateCmd = Assert.Single(claim3.Commands);
        Assert.Equal("Create", folderCreateCmd.CommandType);

        // 5. Complete status folder create command with confirmed browser node ID "777"
        using var completeResponse = await client.PostAsJsonAsync(
            $"/api/extension/commands/{folderCreateCmd.OperationId}/complete",
            new CompletionRequest
            {
                LeaseId = folderCreateCmd.LeaseId,
                Status = "Succeeded",
                BrowserNodeId = "777"
            });
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

        // 6. Claim again -> dependent move command is promoted and contains parentBrowserNodeId = "777"
        var claim4 = await ClaimAsync(client);
        var moveCmd = Assert.Single(claim4.Commands);
        Assert.Equal("Move", moveCmd.CommandType);
        Assert.Equal(bookmark.Id, moveCmd.BookmarkId);

        var payloadJson = JsonSerializer.Serialize(moveCmd.Payload);
        using var doc = JsonDocument.Parse(payloadJson);
        Assert.Equal("777", doc.RootElement.GetProperty("parentBrowserNodeId").GetString());
    }

    [Fact]
    public async Task CreateBookmark_WithExplicitStatus_ProjectsIntoStatusFolder()
    {
        using var client = Factory.CreateClient();

        // 1. Create root "Manga" folder
        using var folderResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Manga" });
        var mangaFolder = (await folderResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        // 2. Create bookmark with explicit "Plan to Read" status under Manga
        using var bmResponse = await client.PostAsJsonAsync(
            $"/api/bookmarks/{mangaFolder.Id}",
            new CreateBookmarkRequest
            {
                Type = NodeType.Bookmark,
                Title = "Choujin X",
                Url = "https://mangaplus.shueisha.co.jp/titles/choujin-x",
                Status = BookmarkReadingStatus.PlanToRead
            });
        bmResponse.EnsureSuccessStatusCode();
        var bookmark = (await bmResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        Assert.Equal(BookmarkReadingStatus.PlanToRead, bookmark.Metadata?.Status);
        Assert.NotEqual(mangaFolder.Id, bookmark.ParentId);

        // Verify parent folder is "Plan to Read"
        using var parentResponse = await client.GetAsync($"/api/bookmarks/{bookmark.ParentId}");
        parentResponse.EnsureSuccessStatusCode();
        var parentFolder = (await parentResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;
        Assert.Equal("Plan to Read", parentFolder.Title);
        Assert.Equal(mangaFolder.Id, parentFolder.ParentId);
    }

    [Fact]
    public async Task CreateBookmark_WithExplicitCompletedStatus_ProducesNoIntermediatePlanToReadFolderOrMoveCommand()
    {
        using var client = Factory.CreateClient();

        // 1. Create root "Manga" folder and confirm it
        using var folderResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Manga" });
        var mangaFolder = (await folderResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        var claim1 = await ClaimAsync(client);
        var mangaFolderCmd = Assert.Single(claim1.Commands);
        using var complete1 = await client.PostAsJsonAsync(
            $"/api/extension/commands/{mangaFolderCmd.OperationId}/complete",
            new CompletionRequest { LeaseId = mangaFolderCmd.LeaseId, Status = "Succeeded", BrowserNodeId = "10" });
        Assert.Equal(HttpStatusCode.OK, complete1.StatusCode);

        // 2. Create bookmark with explicit "Completed" status under Manga (with a series root URL that would have triggered PlanToRead heuristic if status were omitted)
        using var bmResponse = await client.PostAsJsonAsync(
            $"/api/bookmarks/{mangaFolder.Id}",
            new CreateBookmarkRequest
            {
                Type = NodeType.Bookmark,
                Title = "Completed Series",
                Url = "https://mangadex.org/title/completed-series",
                Status = BookmarkReadingStatus.Completed
            });
        bmResponse.EnsureSuccessStatusCode();
        var bookmark = (await bmResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        Assert.Equal(BookmarkReadingStatus.Completed, bookmark.Metadata?.Status);

        // 3. Verify no "Plan to Read" folder was created under Manga
        using var mangaTreeResponse = await client.GetAsync("/api/folders/tree");
        mangaTreeResponse.EnsureSuccessStatusCode();
        var tree = (await mangaTreeResponse.Content.ReadFromJsonAsync<List<FolderTreeNodeDto>>(Options))!;
        var mangaNode = tree.FirstOrDefault(f => f.Id == mangaFolder.Id);
        Assert.NotNull(mangaNode);
        Assert.DoesNotContain(mangaNode.Children, c => c.Title == "Plan to Read");
        Assert.Contains(mangaNode.Children, c => c.Title == "Completed");

        // 4. Claim commands -> exactly status folder Create ("Completed") is ready; bookmark Create is deferred under it; NO Move commands anywhere
        var claim2 = await ClaimAsync(client);
        Assert.DoesNotContain(claim2.Commands, c => c.CommandType == "Move");
        var folderCreateCmd = Assert.Single(claim2.Commands);
        Assert.Equal("Create", folderCreateCmd.CommandType);

        // 5. Complete status folder Create with confirmed browser ID "200"
        using var completeFolder = await client.PostAsJsonAsync(
            $"/api/extension/commands/{folderCreateCmd.OperationId}/complete",
            new CompletionRequest { LeaseId = folderCreateCmd.LeaseId, Status = "Succeeded", BrowserNodeId = "200" });
        Assert.Equal(HttpStatusCode.OK, completeFolder.StatusCode);

        // 6. Claim again -> deferred bookmark Create is promoted with parentBrowserNodeId = "200"
        var claim3 = await ClaimAsync(client);
        Assert.DoesNotContain(claim3.Commands, c => c.CommandType == "Move");
        var bmCreateCmd = Assert.Single(claim3.Commands);
        Assert.Equal("Create", bmCreateCmd.CommandType);
        Assert.Equal(bookmark.Id, bmCreateCmd.BookmarkId);

        var payloadJson = JsonSerializer.Serialize(bmCreateCmd.Payload);
        using var doc = JsonDocument.Parse(payloadJson);
        Assert.Equal("200", doc.RootElement.GetProperty("parentBrowserNodeId").GetString());
    }

    [Fact]
    public async Task CreateBookmark_WithPlanToReadHeuristic_ProjectsIntoPlanToReadFolder()
    {
        using var client = Factory.CreateClient();

        // 1. Create root "Manga" folder
        using var folderResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Manga" });
        var mangaFolder = (await folderResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        // 2. Create bookmark with series root URL that qualifies for PlanToRead heuristic
        using var bmResponse = await client.PostAsJsonAsync(
            $"/api/bookmarks/{mangaFolder.Id}",
            new CreateBookmarkRequest
            {
                Type = NodeType.Bookmark,
                Title = "Gachiakuta",
                Url = "https://mangadex.org/title/gachiakuta"
            });
        bmResponse.EnsureSuccessStatusCode();
        var bookmark = (await bmResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        Assert.Equal(BookmarkReadingStatus.PlanToRead, bookmark.Metadata?.Status);
        Assert.NotEqual(mangaFolder.Id, bookmark.ParentId);

        using var parentResponse = await client.GetAsync($"/api/bookmarks/{bookmark.ParentId}");
        parentResponse.EnsureSuccessStatusCode();
        var parentFolder = (await parentResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;
        Assert.Equal("Plan to Read", parentFolder.Title);
    }

    [Fact]
    public async Task UpdateMetadata_WithInvalidStatus_Returns400BadRequest()
    {
        using var client = Factory.CreateClient();

        // 1. Create root "Manga" folder and bookmark
        using var folderResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Manga" });
        var mangaFolder = (await folderResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        using var bmResponse = await client.PostAsJsonAsync(
            $"/api/bookmarks/{mangaFolder.Id}",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = "Test Title", Url = "https://example.com/manga" });
        var bookmark = (await bmResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        // 2. Try to update metadata with invalid arbitrary status
        using var metaResponse = await client.PutAsJsonAsync(
            $"/api/bookmarks/{bookmark.Id}/metadata",
            new BookmarkMetadataDto { Status = "ArbitraryBypassStatus" });

        Assert.Equal(HttpStatusCode.BadRequest, metaResponse.StatusCode);
    }

    [Fact]
    public async Task UpdateMetadata_WithValidStatusChange_UpdatesStatusAndProjectsFolder()
    {
        using var client = Factory.CreateClient();

        // 1. Create root "Manga" folder and bookmark
        using var folderResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Manga" });
        var mangaFolder = (await folderResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        using var bmResponse = await client.PostAsJsonAsync(
            $"/api/bookmarks/{mangaFolder.Id}",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = "Gantz", Url = "https://mangadex.org/title/gantz" });
        var bookmark = (await bmResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        // 2. Update metadata with valid status "Completed" and tags
        using var metaResponse = await client.PutAsJsonAsync(
            $"/api/bookmarks/{bookmark.Id}/metadata",
            new BookmarkMetadataDto { Status = BookmarkReadingStatus.Completed, Tags = ["Sci-Fi", "Action"] });
        metaResponse.EnsureSuccessStatusCode();
        var updated = (await metaResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options))!;

        Assert.Equal(BookmarkReadingStatus.Completed, updated.Metadata?.Status);
        Assert.Equal(["Sci-Fi", "Action"], updated.Metadata?.Tags);
        Assert.NotEqual(mangaFolder.Id, updated.ParentId);
    }

    private static async Task<ClaimResponse> ClaimAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/extension/commands/claim",
            new ClaimRequest { ConfigVersion = 1, MaxCommands = 10 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClaimResponse>(Options))!;
    }
}
