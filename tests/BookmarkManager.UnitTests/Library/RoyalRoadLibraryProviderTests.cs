using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class RoyalRoadLibraryProviderTests
{
    [Fact]
    public void ParseSearchResults_ExtractsMultipleFictionCards()
    {
        const string html = """
        <div class="fiction-list-item row">
          <div class="cover-col">
            <img src="https://www.royalroadcdn.com/covers/21220.jpg" class="cover" />
          </div>
          <div class="col">
            <h2 class="fiction-title"><a href="/fiction/21220/mother-of-learning">Mother of Learning</a></h2>
            <div class="fiction-description">A time loop story <b>about</b> a mage.</div>
            <span class="tags">
              <a class="fiction-tag" href="/fictions/search?tagsAdd=fantasy">Fantasy</a>
              <a class="fiction-tag" href="/fictions/search?tagsAdd=time-loop">Time Loop</a>
            </span>
          </div>
        </div>
        <div class="fiction-list-item row">
          <div class="cover-col">
            <img src="https://www.royalroadcdn.com/covers/99999.jpg" class="cover" />
          </div>
          <div class="col">
            <h2 class="fiction-title"><a href="/fiction/99999/second-fiction">Second Fiction</a></h2>
            <div class="fiction-description">Another synopsis.</div>
          </div>
        </div>
        """;

        var results = RoyalRoadLibraryProvider.ParseSearchResults(html, "RoyalRoad");

        Assert.Equal(2, results.Count);
        var first = results[0];
        Assert.Equal("21220", first.ProviderId);
        Assert.Equal("Mother of Learning", first.Title);
        Assert.Equal(LibraryMediaType.Webnovel, first.MediaType);
        Assert.Equal("https://www.royalroadcdn.com/covers/21220.jpg", first.CoverImageUrl);
        Assert.Equal("A time loop story about a mage.", first.Synopsis);
        Assert.Equal(new[] { "Fantasy", "Time Loop" }, first.Genres);
        Assert.Equal("https://www.royalroad.com/fiction/21220/mother-of-learning", first.SourceUrl);

        var second = results[1];
        Assert.Equal("99999", second.ProviderId);
        Assert.Equal("Second Fiction", second.Title);
        Assert.Empty(second.Genres);
    }

    [Fact]
    public void ParseSearchResults_ReturnsEmptyWhenNoCardsPresent()
    {
        var results = RoyalRoadLibraryProvider.ParseSearchResults("<html><body>no results</body></html>", "RoyalRoad");
        Assert.Empty(results);
    }

    [Fact]
    public void ParseFictionPage_ReadsOpenGraphMetaAndStatus()
    {
        const string html = """
        <html>
        <head>
          <meta property="og:title" content="Mother of Learning" />
          <meta property="og:image" content="https://www.royalroadcdn.com/covers/21220.jpg" />
          <meta property="og:description" content="A time loop story about a mage." />
        </head>
        <body>
          <span class="label label-default bg-blue-hoki">COMPLETED</span>
          <span class="tags">
            <a class="fiction-tag" href="/fictions/search?tagsAdd=fantasy">Fantasy</a>
          </span>
        </body>
        </html>
        """;

        var entry = RoyalRoadLibraryProvider.ParseFictionPage(html, "21220", "RoyalRoad");

        Assert.NotNull(entry);
        Assert.Equal("Mother of Learning", entry!.Title);
        Assert.Equal("https://www.royalroadcdn.com/covers/21220.jpg", entry.CoverImageUrl);
        Assert.Equal("A time loop story about a mage.", entry.Synopsis);
        Assert.Equal("COMPLETED", entry.Status);
        Assert.Contains("Fantasy", entry.Genres);
    }

    [Fact]
    public void ParseFictionPage_ReturnsNullWhenOgTitleMissing()
    {
        Assert.Null(RoyalRoadLibraryProvider.ParseFictionPage("<html><body>gone</body></html>", "1", "RoyalRoad"));
    }
}
