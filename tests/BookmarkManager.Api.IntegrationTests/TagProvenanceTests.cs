using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class TagProvenanceTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task BulkSaveTags_WritesManualProvenance_AndGetReturnsIt()
    {
        using var client = Factory.CreateClient();
        var bookmark = await CreateBookmarkAsync(client, "Solo Leveling");

        var saveRequest = new BulkSaveTagsRequest
        {
            Tags = new Dictionary<Guid, List<string>> { [bookmark.Id] = ["Action", "Manhwa"] }
        };
        using var saveResponse = await client.PostAsJsonAsync("/api/bookmarks/tags/bulk-save", saveRequest);
        saveResponse.EnsureSuccessStatusCode();

        var provenance = await client.GetFromJsonAsync<List<TagProvenanceDto>>(
            $"/api/bookmarks/{bookmark.Id}/tag-provenance", Options);

        Assert.NotNull(provenance);
        Assert.Equal(2, provenance!.Count);
        Assert.All(provenance, row =>
        {
            Assert.Equal("Manual", row.Provider);
            Assert.Null(row.Confidence);
        });
        Assert.Contains(provenance, row => row.Tag == "Action");
        Assert.Contains(provenance, row => row.Tag == "Manhwa");
    }

    [Fact]
    public async Task BulkSaveTags_SecondSave_ReplacesProvenanceWithoutDuplicates()
    {
        using var client = Factory.CreateClient();
        var bookmark = await CreateBookmarkAsync(client, "Overlord");

        using var firstSave = await client.PostAsJsonAsync("/api/bookmarks/tags/bulk-save", new BulkSaveTagsRequest
        {
            Tags = new Dictionary<Guid, List<string>> { [bookmark.Id] = ["Novel", "Fantasy"] }
        });
        firstSave.EnsureSuccessStatusCode();

        using var secondSave = await client.PostAsJsonAsync("/api/bookmarks/tags/bulk-save", new BulkSaveTagsRequest
        {
            Tags = new Dictionary<Guid, List<string>> { [bookmark.Id] = ["Novel", "Isekai"] }
        });
        secondSave.EnsureSuccessStatusCode();

        var provenance = await client.GetFromJsonAsync<List<TagProvenanceDto>>(
            $"/api/bookmarks/{bookmark.Id}/tag-provenance", Options);

        Assert.NotNull(provenance);
        Assert.Equal(2, provenance!.Count);
        Assert.Contains(provenance, row => row.Tag == "Novel");
        Assert.Contains(provenance, row => row.Tag == "Isekai");
        Assert.DoesNotContain(provenance, row => row.Tag == "Fantasy");
    }

    [Fact]
    public async Task GetTagProvenance_ReturnsEmptyList_ForUnknownBookmark()
    {
        using var client = Factory.CreateClient();

        var provenance = await client.GetFromJsonAsync<List<TagProvenanceDto>>(
            $"/api/bookmarks/{Guid.NewGuid()}/tag-provenance", Options);

        Assert.NotNull(provenance);
        Assert.Empty(provenance!);
    }

    [Fact]
    public async Task RerunTags_WithEmptyIdList_ReturnsProblemDetails400()
    {
        using var client = Factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/bookmarks/rerun-tags",
            new RerunBookmarksRequestDto { BookmarkIds = [] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<BookmarkNodeDto> CreateBookmarkAsync(HttpClient client, string title)
    {
        using var folderResponse = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Provenance" });
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
