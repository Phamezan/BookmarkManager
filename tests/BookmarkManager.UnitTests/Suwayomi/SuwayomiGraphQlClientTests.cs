using System.Net;
using System.Text;
using BookmarkManager.Api.Services.Suwayomi;
using Xunit;

namespace BookmarkManager.UnitTests.Suwayomi;

public sealed class SuwayomiGraphQlClientTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _response;

        public CapturingHandler(string response, HttpStatusCode status = HttpStatusCode.OK)
        {
            _response = response;
            _status = status;
        }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
        }
    }

    private static SuwayomiGraphQlClient Create(HttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://suwayomi:4567/") });

    [Fact]
    public async Task SearchAsync_SendsMutationWithSourceAndQuery_AndParsesResults()
    {
        var handler = new CapturingHandler(
            """{"data":{"fetchSourceManga":{"mangas":[{"id":2153,"title":"Nano Machine","inLibrary":true}]}}}""");
        var client = Create(handler);

        var results = await client.SearchAsync("6247824327199706550", "Nano Machine", default);

        Assert.Single(results);
        Assert.Equal(2153, results[0].Id);
        Assert.True(results[0].InLibrary);
        Assert.Contains("fetchSourceManga", handler.LastBody);
        Assert.Contains("6247824327199706550", handler.LastBody);
        Assert.Contains("Nano Machine", handler.LastBody);
    }

    [Fact]
    public async Task GraphQlErrors_ThrowSuwayomiException()
    {
        var handler = new CapturingHandler("""{"errors":[{"message":"boom"}]}""");
        var client = Create(handler);

        var ex = await Assert.ThrowsAsync<SuwayomiException>(() => client.SearchAsync("1", "x", default));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task NonSuccessStatus_ThrowsSuwayomiException()
    {
        var handler = new CapturingHandler("nope", HttpStatusCode.InternalServerError);
        var client = Create(handler);

        await Assert.ThrowsAsync<SuwayomiException>(() => client.GetStatusAsync(default));
    }

    [Fact]
    public async Task GetMangaAndChapters_ParsesMangaAndChapters()
    {
        var handler = new CapturingHandler(
            """{"data":{"fetchMangaAndChapters":{"manga":{"id":2153,"title":"Nano Machine","realUrl":"https://asurascans.com/comics/nano-machine","thumbnailUrl":"/api/v1/manga/2153/thumbnail"},"chapters":[{"id":1,"chapterNumber":0},{"id":2,"chapterNumber":330.5}]}}}""");
        var client = Create(handler);

        var details = await client.GetMangaAndChaptersAsync(2153, default);

        Assert.Equal("Nano Machine", details.Manga.Title);
        Assert.Equal("https://asurascans.com/comics/nano-machine", details.Manga.RealUrl);
        Assert.Equal(2, details.Chapters.Count);
        Assert.Equal(330.5, details.Chapters[1].ChapterNumber, 3);
    }

    [Fact]
    public async Task GetLibraryAsync_ResumesFirstUnreadThenLatestReadThenSeries()
    {
        var handler = new CapturingHandler("""
            {"data":{"mangas":{"nodes":[
              {"id":5,"title":"Reading","realUrl":"https://example.com/reading","source":{"displayName":"Source"},
               "firstUnreadChapter":{"sourceOrder":0},"latestReadChapter":{"chapterNumber":12.5,"sourceOrder":27},
               "highestNumberedChapter":{"chapterNumber":30}},
              {"id":6,"title":"Caught up","firstUnreadChapter":null,
               "latestReadChapter":{"chapterNumber":30,"sourceOrder":42}},
              {"id":7,"title":"Empty","firstUnreadChapter":null,"latestReadChapter":null}
            ]}}}
            """);
        var client = Create(handler);

        var library = await client.GetLibraryAsync(default);

        Assert.Equal(3, library.Count);
        Assert.Equal(0, library[0].ResumeSourceOrder);
        Assert.Equal(12.5, library[0].LatestReadChapter);
        Assert.Equal(30, library[0].HighestNumberedChapter);
        Assert.Equal(5, library[0].Id);
        Assert.Equal("https://example.com/reading", library[0].RealUrl);
        Assert.Equal("Source", library[0].SourceDisplayName);
        Assert.Equal(42, library[1].ResumeSourceOrder);
        Assert.Null(library[2].ResumeSourceOrder);
        Assert.Contains("firstUnreadChapter{ sourceOrder }", handler.LastBody);
        Assert.Contains("latestReadChapter{ chapterNumber sourceOrder }", handler.LastBody);
        Assert.DoesNotContain("lastReadChapter", handler.LastBody);
    }

    [Fact]
    public async Task GetMangaDetails_ParsesDescriptionAndStatus()
    {
        var handler = new CapturingHandler("""
            {"data":{"fetchMangaAndChapters":{"manga":{"title":"Solo Leveling",
            "genre":["Action","Fantasy"],"description":"A hunter's second chance.","status":"ONGOING"},"chapters":[]}}}
            """);

        var details = await Create(handler).GetMangaDetailsAsync(42, default);

        Assert.Equal("A hunter's second chance.", details.Description);
        Assert.Equal("ONGOING", details.Status);
        Assert.Equal(new[] { "Action", "Fantasy" }, details.Genres);
        Assert.Contains("description", handler.LastBody);
    }

    [Fact]
    public async Task MarkChaptersRead_NoIds_DoesNotCallServer()
    {
        var handler = new CapturingHandler("""{"data":{"updateChapters":{"chapters":[]}}}""");
        var client = Create(handler);

        await client.MarkChaptersReadAsync([], default);

        Assert.Null(handler.LastBody);
    }
}
