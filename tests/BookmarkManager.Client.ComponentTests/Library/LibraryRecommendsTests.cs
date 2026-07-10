using BookmarkManager.Client.Features.Library;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.ComponentTests.LibraryFeatures;

public sealed class LibraryRecommendsTests
{
    [Fact]
    public void BuildRail_EmptyPool_ReturnsEmpty()
    {
        var rail = LibraryRecommends.BuildRail([], 12, seed: 1);
        Assert.Empty(rail);
    }

    [Fact]
    public void BuildRail_SameSeed_IsDeterministic()
    {
        var pool = MakePool(30);
        var first = LibraryRecommends.BuildRail(pool, 12, seed: 42).Select(x => x.ProviderId).ToList();
        var second = LibraryRecommends.BuildRail(pool, 12, seed: 42).Select(x => x.ProviderId).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void BuildRail_FavorsHighScoredTitlesAcrossShuffles()
    {
        var pool = new List<LibraryItem>
        {
            MakeItem("1", "Top Pick", rating: 9.5, daysAgo: 1, trending: true),
            MakeItem("2", "Mid Pick", rating: 6.0, daysAgo: 30, trending: false),
            MakeItem("3", "Low Pick", rating: 3.0, daysAgo: 90, trending: false),
            MakeItem("4", "Strong Two", rating: 8.8, daysAgo: 2, trending: true),
            MakeItem("5", "Average", rating: 5.5, daysAgo: 45, trending: false),
            MakeItem("6", "Weak", rating: 2.5, daysAgo: 120, trending: false),
        };

        var topIds = new HashSet<string> { "1", "4" };
        var topRailHits = 0;

        for (var seed = 1; seed <= 10; seed++)
        {
            var rail = LibraryRecommends.BuildRail(pool, take: 3, seed);
            if (rail.Any(item => topIds.Contains(item.ProviderId)))
                topRailHits++;
        }

        Assert.True(topRailHits >= 8, $"Expected top-scored picks in most shuffles; got {topRailHits}/10");
    }

    [Fact]
    public void BuildRail_DeprioritizesBookmarkedWebnovels()
    {
        var bookmarked = MakeItem("already-reading", "Already Reading", rating: 9.8, daysAgo: 1, trending: true);
        var fresh = MakeItem("new-pick", "New Pick", rating: 7.0, daysAgo: 3, trending: true);
        var pool = new List<LibraryItem> { bookmarked, fresh };

        var exclusions = LibraryBookmarkExclusions.FromBookmarks([
            new BookmarkSignal("https://novelfire.net/book/already-reading", "Already Reading", null)
        ]);

        var bookmarkedHits = 0;
        for (var seed = 1; seed <= 20; seed++)
        {
            var rail = LibraryRecommends.BuildRail(pool, take: 1, seed, exclusions);
            if (rail[0].ProviderId == "already-reading")
                bookmarkedHits++;
        }

        Assert.True(bookmarkedHits <= 6, $"Bookmarked lead should rarely win; got {bookmarkedHits}/20");
    }

    private static List<LibraryItem> MakePool(int count) =>
        Enumerable.Range(1, count)
                  .Select(i => MakeItem(i.ToString(), $"Title {i}", rating: 4 + i % 5, daysAgo: i, trending: i % 3 == 0))
                  .ToList();

    private static LibraryItem MakeItem(string id, string title, double rating, int daysAgo, bool trending)
    {
        var dto = new LibraryEntryDto(
            "Novelfire", id, title, [], [], LibraryMediaType.Webnovel, null, "Synopsis",
            ["Fantasy"], rating, "Ongoing", "10", null,
            DateTimeOffset.UtcNow.AddDays(-daysAgo), $"https://novelfire.net/book/{id}");
        return LibraryItem.FromDto(dto, trending);
    }
}
