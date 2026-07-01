using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class BookmarksTagsTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task GetTags_FiltersByFolderRecursively()
    {
        using var client = Factory.CreateClient();

        // 1. Create a root folder "Manga"
        using var folder1Response = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Manga" });
        folder1Response.EnsureSuccessStatusCode();
        var folder1 = await folder1Response.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(folder1);

        // 2. Create a subfolder "Action" under "Manga"
        using var folder2Response = await client.PostAsJsonAsync(
            $"/api/bookmarks/{folder1!.Id}",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Action" });
        folder2Response.EnsureSuccessStatusCode();
        var folder2 = await folder2Response.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(folder2);

        // 3. Create another root folder "Novels"
        using var folder3Response = await client.PostAsJsonAsync(
            "/api/bookmarks/00000000-0000-0000-0000-000000000000",
            new CreateBookmarkRequest { Type = NodeType.Folder, Title = "Novels" });
        folder3Response.EnsureSuccessStatusCode();
        var folder3 = await folder3Response.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(folder3);

        // 4. Create a bookmark under "Action" subfolder with tags "Action", "Supernatural"
        using var bookmark1Response = await client.PostAsJsonAsync(
            $"/api/bookmarks/{folder2!.Id}",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = "Solo Leveling", Url = "https://example.com/sl" });
        bookmark1Response.EnsureSuccessStatusCode();
        var bookmark1 = await bookmark1Response.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(bookmark1);
        
        using var meta1Response = await client.PutAsJsonAsync($"/api/bookmarks/{bookmark1!.Id}/metadata", new BookmarkMetadataDto { Tags = ["Action", "Supernatural"] });
        meta1Response.EnsureSuccessStatusCode();

        // 5. Create a bookmark under "Novels" root folder with tags "Novel", "Fantasy"
        using var bookmark2Response = await client.PostAsJsonAsync(
            $"/api/bookmarks/{folder3!.Id}",
            new CreateBookmarkRequest { Type = NodeType.Bookmark, Title = "Overlord", Url = "https://example.com/overlord" });
        bookmark2Response.EnsureSuccessStatusCode();
        var bookmark2 = await bookmark2Response.Content.ReadFromJsonAsync<BookmarkNodeDto>(Options);
        Assert.NotNull(bookmark2);
        
        using var meta2Response = await client.PutAsJsonAsync($"/api/bookmarks/{bookmark2!.Id}/metadata", new BookmarkMetadataDto { Tags = ["Novel", "Fantasy"] });
        meta2Response.EnsureSuccessStatusCode();

        // 6. Query tags globally -> should return all tags: Action, Supernatural, Novel, Fantasy
        var globalTags = await client.GetFromJsonAsync<List<TagCountDto>>("/api/bookmarks/tags", Options);
        Assert.NotNull(globalTags);
        Assert.Contains(globalTags, t => t.Tag == "Action");
        Assert.Contains(globalTags, t => t.Tag == "Supernatural");
        Assert.Contains(globalTags, t => t.Tag == "Novel");
        Assert.Contains(globalTags, t => t.Tag == "Fantasy");

        // 7. Query tags under "Manga" folder recursively -> should only return tags from its subfolder: Action, Supernatural
        var mangaTags = await client.GetFromJsonAsync<List<TagCountDto>>($"/api/bookmarks/tags?folderId={folder1.Id}", Options);
        Assert.NotNull(mangaTags);
        Assert.Contains(mangaTags, t => t.Tag == "Action");
        Assert.Contains(mangaTags, t => t.Tag == "Supernatural");
        Assert.DoesNotContain(mangaTags, t => t.Tag == "Novel");
        Assert.DoesNotContain(mangaTags, t => t.Tag == "Fantasy");

        // 8. Query tags under "Action" folder -> should only return Action, Supernatural
        var actionTags = await client.GetFromJsonAsync<List<TagCountDto>>($"/api/bookmarks/tags?folderId={folder2.Id}", Options);
        Assert.NotNull(actionTags);
        Assert.Contains(actionTags, t => t.Tag == "Action");
        Assert.Contains(actionTags, t => t.Tag == "Supernatural");

        // 9. Query tags under "Novels" folder -> should only return Novel, Fantasy
        var novelTags = await client.GetFromJsonAsync<List<TagCountDto>>($"/api/bookmarks/tags?folderId={folder3.Id}", Options);
        Assert.NotNull(novelTags);
        Assert.Contains(novelTags, t => t.Tag == "Novel");
        Assert.Contains(novelTags, t => t.Tag == "Fantasy");
        Assert.DoesNotContain(novelTags, t => t.Tag == "Action");
    }
}
