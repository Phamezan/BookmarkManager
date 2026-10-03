using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class BookmarkCreatedAtTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static async Task<BookmarkNodeDto> CreateBookmarkAsync(HttpClient client, string title)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = title, Url = $"https://example.com/{title}" });
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(dto);
        return dto!;
    }

    [Fact]
    public async Task CreateBookmark_SetsCreatedAt_AndUpdateAndTag_DoNotChangeIt()
    {
        using var client = Factory.CreateClient();

        var created = await CreateBookmarkAsync(client, "Created Once");
        Assert.NotEqual(default, created.CreatedAt);
        Assert.Equal(created.UpdatedAt, created.CreatedAt, TimeSpan.FromSeconds(1));

        // Tag via metadata update — bumps UpdatedAt, must not touch CreatedAt.
        using (var metaResponse = await client.PutAsJsonAsync(
            $"/api/bookmarks/{created.Id}/metadata",
            new BookmarkMetadataDto { Tags = ["action"] }))
        {
            metaResponse.EnsureSuccessStatusCode();
        }

        // Title update — same rule.
        using (var updateResponse = await client.PutAsJsonAsync(
            $"/api/bookmarks/{created.Id}",
            new UpdateBookmarkRequest { Title = "Renamed", Url = "https://example.com/renamed" }))
        {
            updateResponse.EnsureSuccessStatusCode();
        }

        var reloaded = await client.GetFromJsonAsync<BookmarkNodeDto>($"/api/bookmarks/{created.Id}", Options);
        Assert.NotNull(reloaded);
        Assert.Equal(created.CreatedAt, reloaded!.CreatedAt);
        Assert.True(reloaded.UpdatedAt >= created.UpdatedAt);
    }

    [Fact]
    public async Task Search_SortByCreated_OrdersByCreatedAtDesc_WhileUpdatedKeepsHistoryOrder()
    {
        using var client = Factory.CreateClient();

        var first = await CreateBookmarkAsync(client, "First");
        await Task.Delay(20);
        var second = await CreateBookmarkAsync(client, "Second");
        await Task.Delay(20);
        var third = await CreateBookmarkAsync(client, "Third");

        // Touch the oldest so UpdatedAt now ranks it first under the historical ordering.
        using (var metaResponse = await client.PutAsJsonAsync(
            $"/api/bookmarks/{first.Id}/metadata",
            new BookmarkMetadataDto { Tags = ["touched"] }))
        {
            metaResponse.EnsureSuccessStatusCode();
        }

        var byCreated = await SearchAsync(client, new SearchRequest { Query = "", PageSize = 10, SortBy = "Created" });
        Assert.Equal([third.Id, second.Id, first.Id], byCreated.Items.Select(i => i.Id).ToList());

        var byUpdated = await SearchAsync(client, new SearchRequest { Query = "", PageSize = 10 });
        Assert.Equal(first.Id, byUpdated.Items[0].Id);

        var unknownSort = await SearchAsync(client, new SearchRequest { Query = "", PageSize = 10, SortBy = "not-a-real-sort" });
        Assert.Equal(byUpdated.Items.Select(i => i.Id), unknownSort.Items.Select(i => i.Id));
    }

    private static async Task<PagedResult<BookmarkNodeDto>> SearchAsync(HttpClient client, SearchRequest request)
    {
        using var response = await client.PostAsJsonAsync("/api/search", request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<BookmarkNodeDto>>(Options);
        Assert.NotNull(result);
        return result!;
    }
}
