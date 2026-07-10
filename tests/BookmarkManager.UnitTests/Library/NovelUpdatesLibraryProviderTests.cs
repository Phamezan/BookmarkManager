using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class NovelUpdatesLibraryProviderTests
{
    [Fact]
    public void ParseSearchResults_ExtractsSlugAsProviderId()
    {
        const string html = """
        <div class="search_main_box_nu">
          <div class="search_title"><a href="https://www.novelupdates.com/series/a-monster-who-levels-up/">A Monster Who Levels Up</a></div>
        </div>
        <div class="search_main_box_nu">
          <div class="search_title"><a href="https://www.novelupdates.com/series/second-novel/">Second Novel</a></div>
        </div>
        """;

        var results = NovelUpdatesLibraryProvider.ParseSearchResults(html, "NovelUpdates");

        Assert.Equal(2, results.Count);
        Assert.Equal("a-monster-who-levels-up", results[0].ProviderId);
        Assert.Equal("A Monster Who Levels Up", results[0].Title);
        Assert.Equal(LibraryMediaType.Webnovel, results[0].MediaType);
        Assert.Equal("https://www.novelupdates.com/series/a-monster-who-levels-up/", results[0].SourceUrl);
        Assert.Equal("second-novel", results[1].ProviderId);
    }

    [Fact]
    public void ParseSearchResults_ReturnsEmptyWhenNoMatches()
    {
        var results = NovelUpdatesLibraryProvider.ParseSearchResults("<html><body>nothing</body></html>", "NovelUpdates");
        Assert.Empty(results);
    }

    [Fact]
    public void ParseSeriesPage_ExtractsCoverSynopsisGenresAuthorsAndLatestChapter()
    {
        const string html = """
        <div class="seriestitlenu">A Monster Who Levels Up</div>
        <div class="seriesimg">
          <img src="https://cdn.novelupdates.com/images/2020/monster.jpg" />
        </div>
        <div id="editdescription">
          <p>A hunter fights monsters and levels up.</p>
        </div>
        <div id="seriesgenre">
          <a href="/genre/action/">Action</a>
          <a href="/genre/fantasy/">Fantasy</a>
        </div>
        <div id="showauthors">
          <a href="/series-author/some-author/">Some Author</a>
        </div>
        <table id="myTable">
          <tbody>
            <tr>
              <td><a href="/chapter-link/">c123</a></td>
            </tr>
          </tbody>
        </table>
        """;

        var entry = NovelUpdatesLibraryProvider.ParseSeriesPage(html, "a-monster-who-levels-up", "NovelUpdates");

        Assert.NotNull(entry);
        Assert.Equal("A Monster Who Levels Up", entry!.Title);
        Assert.Equal("https://cdn.novelupdates.com/images/2020/monster.jpg", entry.CoverImageUrl);
        Assert.Equal("A hunter fights monsters and levels up.", entry.Synopsis);
        Assert.Equal(new[] { "Action", "Fantasy" }, entry.Genres);
        Assert.Equal("Some Author", Assert.Single(entry.Authors));
        Assert.Equal("c123", entry.LatestChapter);
        Assert.Equal("https://www.novelupdates.com/series/a-monster-who-levels-up/", entry.SourceUrl);
    }

    [Fact]
    public void ParseSeriesPage_ReturnsNullWhenTitleMissing()
    {
        Assert.Null(NovelUpdatesLibraryProvider.ParseSeriesPage("<html><body>gone</body></html>", "x", "NovelUpdates"));
    }
}
