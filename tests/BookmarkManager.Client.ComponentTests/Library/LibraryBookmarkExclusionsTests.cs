using BookmarkManager.Client.Features.Library;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.ComponentTests.LibraryFeatures;

public sealed class LibraryBookmarkExclusionsTests
{
    [Fact]
    public void Contains_NovelfireChapterBookmark_MatchesCatalogSeries()
    {
        var exclusions = LibraryBookmarkExclusions.FromBookmarks([
            new BookmarkSignal("https://novelfire.net/book/shadow-slave/chapter-12", "Shadow Slave", null)
        ]);

        var item = MakeWebnovel("Novelfire", "shadow-slave", "Shadow Slave");

        Assert.True(exclusions.Contains(item));
    }

    [Fact]
    public void Contains_ProviderAndHostKeys_Intersect()
    {
        var exclusions = LibraryBookmarkExclusions.FromBookmarks([
            new BookmarkSignal("https://novelfire.net/book/radiant-blade", "Radiant Blade", null)
        ]);

        var item = MakeWebnovel("Novelfire", "radiant-blade", "Radiant Blade");

        Assert.True(exclusions.Contains(item));
    }

    [Fact]
    public void Contains_UnrelatedSeries_ReturnsFalse()
    {
        var exclusions = LibraryBookmarkExclusions.FromBookmarks([
            new BookmarkSignal("https://novelfire.net/book/shadow-slave", "Shadow Slave", null)
        ]);

        var item = MakeWebnovel("Novelfire", "lord-of-mysteries", "Lord of Mysteries");

        Assert.False(exclusions.Contains(item));
    }

    private static LibraryItem MakeWebnovel(string provider, string providerId, string title)
    {
        var dto = new LibraryEntryDto(
            provider, providerId, title, [], [], LibraryMediaType.Webnovel, null, "Synopsis",
            ["Fantasy"], 8.0, "Ongoing", "100", null, DateTimeOffset.UtcNow,
            $"https://novelfire.net/book/{providerId}");
        return LibraryItem.FromDto(dto, isTrending: true);
    }
}
