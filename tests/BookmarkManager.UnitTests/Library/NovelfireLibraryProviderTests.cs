using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class NovelfireLibraryProviderTests
{
    [Fact]
    public void ParseGenreListing_ExtractsSlugTitleCoverAndRating()
    {
        const string html = """
        <ul class="novel-list">
            <li class="novel-item"><a title="Shadow Slave" href="/book/shadow-slave"><figure class="novel-cover"><img class="lazy" src="data:image/gif;base64,x" data-src="/server-1/shadow-slave.jpg" alt="Shadow Slave"><span class="badge _bl"><i class="icon-award"></i>R 1</span><span class="badge _br"><i class="icon-star"></i>4.6</span></figure><h4 class="novel-title text2row">Shadow Slave</h4></a><div class="novel-stats"><i class="icon-book-open"></i> 3090 Chapters</span></div></li>
            <li class="novel-item"><a title="Lord of the Mysteries" href="/book/lord-of-the-mysteries"><figure class="novel-cover"><img class="lazy" src="data:image/gif;base64,x" data-src="/server-1/lord-of-the-mysteries.jpg" alt="Lord of the Mysteries"><span class="badge _bl"><i class="icon-award"></i>R 2</span><span class="badge _br"><i class="icon-star"></i>4.8</span></figure><h4 class="novel-title text2row">Lord of the Mysteries</h4></a><div class="novel-stats"><i class="icon-book-open"></i> 1432 Chapters</span></div></li>
        </ul>
        """;

        var results = NovelfireLibraryProvider.ParseGenreListing(html, "Novelfire");

        Assert.Equal(2, results.Count);
        Assert.Equal("shadow-slave", results[0].ProviderId);
        Assert.Equal("Shadow Slave", results[0].Title);
        Assert.Equal(LibraryMediaType.Webnovel, results[0].MediaType);
        Assert.Equal("https://novelfire.net/server-1/shadow-slave.jpg", results[0].CoverImageUrl);
        Assert.Equal(4.6, results[0].Rating);
        Assert.Equal("3090", results[0].LatestChapter);
        Assert.Equal("https://novelfire.net/book/shadow-slave", results[0].SourceUrl);
        Assert.Equal("lord-of-the-mysteries", results[1].ProviderId);
        Assert.Equal(4.8, results[1].Rating);
        Assert.Equal("1432", results[1].LatestChapter);
    }

    [Fact]
    public void ParseGenreListing_ReturnsEmptyWhenNoCards()
    {
        Assert.Empty(NovelfireLibraryProvider.ParseGenreListing("<html><body>nothing</body></html>", "Novelfire"));
    }

    [Fact]
    public void ParseNovelPage_ExtractsAuthorStatusGenresSynopsisAndLatestChapter()
    {
        const string html = """
        <meta property="og:image" content="https://novelfire.net/server-1/shadow-slave.jpg">
        <meta itemprop="description" content="Growing up in poverty, Sunny never expected anything good from life.">
        <div class="main-head"><h1 itemprop="name" class="novel-title text2row">Shadow Slave</h1>
        <div class="author"><span>Author:</span> <a href="/author/guiltythree" title="Guiltythree" class="property-item"><span itemprop="author">Guiltythree</span></a></div></div>
        <div class="header-stats"><span><strong><i class="icon-book-open"></i> 3090</strong><small>Chapters</small></span>
        <span><strong class="ongoing">Ongoing</strong> <small>Status</small></span></div>
        <div class="categories"><h4>Genres</h4><ul><li><a href="/genre-action/sort-new/status-all/all-novel" title="Action" class="property-item">Action</a></li><li><a href="/genre-fantasy/sort-new/status-all/all-novel" title="Fantasy" class="property-item">Fantasy</a></li></ul></div>
        <a class="grdbtn chapter-latest-container" title="Shadow Slave Novel Chapters" href="https://novelfire.net/book/shadow-slave/chapters">
           <div class="body">
              <h4>Novel Chapters</h4>
              <p class="latest text1row">Chapter 3090 Born Into an Endless War</p>
              <p class="update">Updated 15 hours ago</p>
           </div>
        </a>
        """;

        var entry = NovelfireLibraryProvider.ParseNovelPage(html, "shadow-slave", "Novelfire");

        Assert.NotNull(entry);
        Assert.Equal("Shadow Slave", entry!.Title);
        Assert.Equal("Guiltythree", Assert.Single(entry.Authors));
        Assert.Equal("Ongoing", entry.Status);
        Assert.Equal(new[] { "Action", "Fantasy" }, entry.Genres);
        // The author anchor also has class="property-item" - must not leak into Genres.
        Assert.DoesNotContain("Guiltythree", entry.Genres);
        Assert.Equal("Growing up in poverty, Sunny never expected anything good from life.", entry.Synopsis);
        Assert.Equal("Chapter 3090 Born Into an Endless War", entry.LatestChapter);
        Assert.Equal(LibraryMediaType.Webnovel, entry.MediaType);
        Assert.Equal("https://novelfire.net/server-1/shadow-slave.jpg", entry.CoverImageUrl);
        Assert.Equal("https://novelfire.net/book/shadow-slave", entry.SourceUrl);
        Assert.NotNull(entry.LastReleaseAt);
        Assert.True(entry.LastReleaseAt < DateTimeOffset.UtcNow.AddHours(-14) && entry.LastReleaseAt > DateTimeOffset.UtcNow.AddHours(-16));
    }

    [Fact]
    public void ParseNovelPage_MergesSeparateTagsSectionIntoGenres()
    {
        const string html = """
        <h1 class="novel-title">Lord of the Mysteries</h1>
        <div class="categories"><h4>Genres</h4><ul><li><a href="#" title="Fantasy" class="property-item">Fantasy</a></li><li><a href="#" title="Action" class="property-item">Action</a></li></ul></div>
        <div class="tags mt-lg-1 mt-3 clearfix">
            <h4 class="lined">Tags</h4>
            <div class="expand-wrapper">
                <ul class="content"> <li><a class="tag" href="/tags/gods/order-popular" rel="tag">Gods</a></li>  <li><a class="tag" href="/tags/time-skip/order-popular" rel="tag">Time Skip</a></li>  <li><a class="tag" href="/tags/action/order-popular" rel="tag">Action</a></li> </ul>
                <div class="expand"><a class="expand-btn"><i class="icon-right-open"></i> <span>Show More</span></a></div>
            </div>
        </div>
        """;

        var entry = NovelfireLibraryProvider.ParseNovelPage(html, "lord-of-the-mysteries", "Novelfire");

        Assert.NotNull(entry);
        Assert.Equal(new[] { "Fantasy", "Action", "Gods", "Time Skip" }, entry!.Genres);
    }

    [Fact]
    public void ParseNovelPage_ReturnsNullWhenTitleMissing()
    {
        Assert.Null(NovelfireLibraryProvider.ParseNovelPage("<html><body>gone</body></html>", "x", "Novelfire"));
    }

    [Theory]
    [InlineData("Updated a month ago")]
    [InlineData("Updated an hour ago")]
    public void ParseNovelPage_HandlesSingularRelativeTimePhrasing(string updatedText)
    {
        var html = $"""
        <h1 class="novel-title">Some Novel</h1>
        <a class="chapter-latest-container" href="#">
           <p class="latest text1row">Chapter 1</p>
           <p class="update">{updatedText}</p>
        </a>
        """;

        var entry = NovelfireLibraryProvider.ParseNovelPage(html, "some-novel", "Novelfire");

        Assert.NotNull(entry);
        Assert.NotNull(entry!.LastReleaseAt);
    }

    [Fact]
    public void ParseNovelPage_FallsBackToHeaderChapterCountWhenLatestStripMissing()
    {
        const string html = """
        <h1 class="novel-title">Shadow Slave</h1>
        <meta itemprop="description" content="A real synopsis.">
        <div class="header-stats"><span><strong><i class="icon-book-open"></i> 3090</strong><small>Chapters</small></span></div>
        """;

        var entry = NovelfireLibraryProvider.ParseNovelPage(html, "shadow-slave", "Novelfire");

        Assert.NotNull(entry);
        Assert.Equal("3090", entry!.LatestChapter);
        Assert.Equal("A real synopsis.", entry.Synopsis);
    }

    [Fact]
    public void SearchAsync_AlwaysReturnsEmpty()
    {
        var provider = new NovelfireLibraryProvider(
            new NoOpHttpClientFactory(),
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NovelfireLibraryProvider>.Instance,
            Microsoft.Extensions.Options.Options.Create(new LibraryProviderOptions()));

        var result = provider.SearchAsync("anything", null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Empty(result);
    }

    private sealed class NoOpHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
