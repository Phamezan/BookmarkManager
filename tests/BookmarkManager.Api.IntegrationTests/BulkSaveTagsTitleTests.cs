using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

/// <summary>
/// Phase 5 (auto-tagger review page title edit) bulk-save behavior: title writes
/// must follow the same sync-command path as UpdateAsync (see
/// .cursor/commands/review-sync-change.md), and tag provenance must reflect which
/// rows the user actually touched (see .cursor/commands/review-autotagging-change.md).
/// </summary>
public sealed class BulkSaveTagsTitleTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private AppDbContext GetDb(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    [Fact]
    public async Task BulkSaveTags_WithTitleChange_IncrementsVersionAndEnqueuesUpdateCommand()
    {
        using var client = Factory.CreateClient();
        var bookmark = await CreateBookmarkAsync(client, "Solo Leveling Ch. 1");

        var saveRequest = new BulkSaveTagsRequest
        {
            Tags = new Dictionary<Guid, List<string>> { [bookmark.Id] = ["Action"] },
            Titles = new Dictionary<Guid, string> { [bookmark.Id] = "Solo Leveling Chapter 1 (Edited)" },
            ManuallyEditedTagIds = [bookmark.Id]
        };
        using var saveResponse = await client.PostAsJsonAsync("/api/bookmarks/tags/bulk-save", saveRequest);
        saveResponse.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = GetDb(scope);
        var node = await db.BookmarkNodes.SingleAsync(n => n.Id == bookmark.Id);

        Assert.Equal("Solo Leveling Chapter 1 (Edited)", node.Title);
        Assert.Equal(2, node.Version); // 1 from create, +1 from the title write.
        Assert.Equal(SyncState.Pending, node.SyncState);

        var command = await db.ExtensionCommands.SingleAsync(c => c.BookmarkId == bookmark.Id && c.CommandType == "Update");
        Assert.Equal(2, command.ExpectedVersion);
        Assert.Contains("Solo Leveling Chapter 1 (Edited)", command.PayloadJson);
        Assert.Equal("Pending", command.Status);
        Assert.Equal("Solo Leveling Ch. 1", node.PreviousTitle);
    }

    [Fact]
    public async Task BulkSaveTags_SecondTitleChange_LeavesPreviousTitleUnchanged()
    {
        using var client = Factory.CreateClient();
        var bookmark = await CreateBookmarkAsync(client, "Original Title");

        var firstSave = new BulkSaveTagsRequest
        {
            Titles = new Dictionary<Guid, string> { [bookmark.Id] = "First Rename" }
        };
        using var firstResponse = await client.PostAsJsonAsync("/api/bookmarks/tags/bulk-save", firstSave);
        firstResponse.EnsureSuccessStatusCode();

        var secondSave = new BulkSaveTagsRequest
        {
            Titles = new Dictionary<Guid, string> { [bookmark.Id] = "Second Rename" }
        };
        using var secondResponse = await client.PostAsJsonAsync("/api/bookmarks/tags/bulk-save", secondSave);
        secondResponse.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = GetDb(scope);
        var node = await db.BookmarkNodes.SingleAsync(n => n.Id == bookmark.Id);

        Assert.Equal("Second Rename", node.Title);
        Assert.Equal("Original Title", node.PreviousTitle);
    }

    [Fact]
    public async Task BulkSaveTags_TagsOnly_NoTitlesProvided_CreatesNoExtensionCommand()
    {
        using var client = Factory.CreateClient();
        var bookmark = await CreateBookmarkAsync(client, "Overlord");

        var saveRequest = new BulkSaveTagsRequest
        {
            Tags = new Dictionary<Guid, List<string>> { [bookmark.Id] = ["Novel", "Isekai"] }
        };
        using var saveResponse = await client.PostAsJsonAsync("/api/bookmarks/tags/bulk-save", saveRequest);
        saveResponse.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = GetDb(scope);
        var node = await db.BookmarkNodes.SingleAsync(n => n.Id == bookmark.Id);

        Assert.Equal("Overlord", node.Title);
        Assert.Equal(1, node.Version); // unchanged - tags are manager-only metadata.
        Assert.Equal(0, await db.ExtensionCommands.CountAsync(c => c.BookmarkId == bookmark.Id && c.CommandType == "Update"));
    }

    [Fact]
    public async Task BulkSaveTags_TitleEqualToCurrent_IsANoOp_NoExtensionCommand()
    {
        using var client = Factory.CreateClient();
        var bookmark = await CreateBookmarkAsync(client, "Mushoku Tensei");

        var saveRequest = new BulkSaveTagsRequest
        {
            Tags = new Dictionary<Guid, List<string>> { [bookmark.Id] = ["Isekai"] },
            Titles = new Dictionary<Guid, string> { [bookmark.Id] = "Mushoku Tensei" }
        };
        using var saveResponse = await client.PostAsJsonAsync("/api/bookmarks/tags/bulk-save", saveRequest);
        saveResponse.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = GetDb(scope);
        var node = await db.BookmarkNodes.SingleAsync(n => n.Id == bookmark.Id);

        Assert.Equal(1, node.Version);
        Assert.Equal(0, await db.ExtensionCommands.CountAsync(c => c.BookmarkId == bookmark.Id && c.CommandType == "Update"));
    }

    [Fact]
    public async Task BulkSaveTags_BlankTitle_IsSkipped_KeepsOriginalTitle()
    {
        using var client = Factory.CreateClient();
        var bookmark = await CreateBookmarkAsync(client, "Kaguya-sama");

        var saveRequest = new BulkSaveTagsRequest
        {
            Titles = new Dictionary<Guid, string> { [bookmark.Id] = "   " }
        };
        using var saveResponse = await client.PostAsJsonAsync("/api/bookmarks/tags/bulk-save", saveRequest);
        saveResponse.EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = GetDb(scope);
        var node = await db.BookmarkNodes.SingleAsync(n => n.Id == bookmark.Id);

        Assert.Equal("Kaguya-sama", node.Title);
        Assert.Equal(1, node.Version);
        Assert.Equal(0, await db.ExtensionCommands.CountAsync(c => c.BookmarkId == bookmark.Id && c.CommandType == "Update"));
    }

    [Fact]
    public async Task BulkSaveTags_ManuallyEditedTagIds_OnlyMarksTouchedRowsManual()
    {
        using var client = Factory.CreateClient();
        var touched = await CreateBookmarkAsync(client, "One Piece");
        var untouched = await CreateBookmarkAsync(client, "Bleach");

        var saveRequest = new BulkSaveTagsRequest
        {
            Tags = new Dictionary<Guid, List<string>>
            {
                [touched.Id] = ["Pirates"],
                [untouched.Id] = ["Shonen"]
            },
            ManuallyEditedTagIds = [touched.Id]
        };
        using var saveResponse = await client.PostAsJsonAsync("/api/bookmarks/tags/bulk-save", saveRequest);
        saveResponse.EnsureSuccessStatusCode();

        var touchedProvenance = await client.GetFromJsonAsync<List<TagProvenanceDto>>(
            $"/api/bookmarks/{touched.Id}/tag-provenance", Options);
        var untouchedProvenance = await client.GetFromJsonAsync<List<TagProvenanceDto>>(
            $"/api/bookmarks/{untouched.Id}/tag-provenance", Options);

        Assert.Equal("Manual", Assert.Single(touchedProvenance!).Provider);
        Assert.Equal("Suggested", Assert.Single(untouchedProvenance!).Provider);
    }

    [Fact]
    public async Task BulkSaveTags_NullManuallyEditedTagIds_MarksEverythingManual()
    {
        // Preserves pre-Phase-5 behavior for the rerun quick-edit path, which never sends this field.
        using var client = Factory.CreateClient();
        var bookmark = await CreateBookmarkAsync(client, "Attack on Titan");

        var saveRequest = new BulkSaveTagsRequest
        {
            Tags = new Dictionary<Guid, List<string>> { [bookmark.Id] = ["Action", "Drama"] }
        };
        using var saveResponse = await client.PostAsJsonAsync("/api/bookmarks/tags/bulk-save", saveRequest);
        saveResponse.EnsureSuccessStatusCode();

        var provenance = await client.GetFromJsonAsync<List<TagProvenanceDto>>(
            $"/api/bookmarks/{bookmark.Id}/tag-provenance", Options);

        Assert.NotNull(provenance);
        Assert.All(provenance!, row => Assert.Equal("Manual", row.Provider));
    }

    private static async Task<BookmarkNodeDto> CreateBookmarkAsync(HttpClient client, string title)
    {
        using var folderResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "BulkSaveTitle" });
        folderResponse.EnsureSuccessStatusCode();
        var folder = await folderResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(folder);

        using var bookmarkResponse = await client.PostAsJsonAsync(
            $"/api/bookmarks/{folder!.Id}",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = title, Url = "https://example.com/x" });
        bookmarkResponse.EnsureSuccessStatusCode();
        var bookmark = await bookmarkResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(bookmark);
        return bookmark!;
    }
}
