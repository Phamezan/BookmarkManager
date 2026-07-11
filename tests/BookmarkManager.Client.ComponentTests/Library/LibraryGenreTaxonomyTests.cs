using BookmarkManager.Client.Features.Library;

namespace BookmarkManager.Client.ComponentTests.LibraryFeatures;

public sealed class LibraryGenreTaxonomyTests
{
    [Fact]
    public void GroupGenres_EmptyInput_ReturnsEmpty()
    {
        var groups = LibraryGenreTaxonomy.GroupGenres([]);
        Assert.Empty(groups);
    }

    [Fact]
    public void GroupGenres_KnownTags_PlaceInExpectedSections()
    {
        var groups = LibraryGenreTaxonomy.GroupGenres(["Isekai", "Action", "Romance", "Cyberpunk"]);

        Assert.Equal(4, groups.Count);
        Assert.Equal("Action & adventure", groups[0].Label);
        Assert.Contains("Action", groups[0].Tags);
        Assert.Equal("Fantasy & isekai", groups[1].Label);
        Assert.Contains("Isekai", groups[1].Tags);
        Assert.Equal("Romance & drama", groups[2].Label);
        Assert.Contains("Romance", groups[2].Tags);
        Assert.Equal("Sci-fi & modern", groups[3].Label);
        Assert.Contains("Cyberpunk", groups[3].Tags);
    }

    [Fact]
    public void GroupGenres_UnknownTags_GoToOtherBucket()
    {
        var groups = LibraryGenreTaxonomy.GroupGenres(["Action", "Zombie Apocalypse"]);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Action & adventure", groups[0].Label);
        Assert.Equal("Other tags", groups[1].Label);
        Assert.Single(groups[1].Tags);
        Assert.Equal("Zombie Apocalypse", groups[1].Tags[0]);
    }
}
