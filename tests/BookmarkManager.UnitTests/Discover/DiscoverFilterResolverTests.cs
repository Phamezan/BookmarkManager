using BookmarkManager.Api.Services.Suwayomi;

namespace BookmarkManager.UnitTests.Discover;

public sealed class DiscoverFilterResolverTests
{
    private static SuwayomiFilter Sort(int position, string name, params string[] values)
        => new(position, name, SuwayomiFilterKind.Sort, values, []);

    private static SuwayomiFilter Select(int position, string name, params string[] values)
        => new(position, name, SuwayomiFilterKind.Select, values, []);

    private static SuwayomiFilter Group(int position, string name, params SuwayomiFilter[] children)
        => new(position, name, SuwayomiFilterKind.Group, [], children);

    private static SuwayomiFilter CheckBox(int position, string name)
        => new(position, name, SuwayomiFilterKind.CheckBox, [], []);

    private static SuwayomiFilter TriState(int position, string name)
        => new(position, name, SuwayomiFilterKind.TriState, [], []);

    [Fact]
    public void Asura_SortFilterUsesSortState_AndGenreGroupCheckBox()
    {
        var filters = new[]
        {
            Sort(0, "Sort By", "Popularity", "Latest Update"),
            Select(1, "Type", "All", "Manhwa"),
            Group(2, "Genres", CheckBox(0, "Action"), CheckBox(1, "Adventure"))
        };

        var plan = DiscoverFilterResolver.Resolve(filters);

        Assert.True(plan.CanRun);
        Assert.Equal(2, plan.Changes.Count);

        var sort = plan.Changes.Single(c => c.Position == 0);
        Assert.NotNull(sort.SortState);
        Assert.Equal(1, sort.SortState!.Index);
        Assert.False(sort.SortState.Ascending);
        Assert.Null(sort.SelectState);

        var genre = plan.Changes.Single(c => c.Position == 2);
        Assert.NotNull(genre.GroupChange);
        Assert.Equal(0, genre.GroupChange!.Position);
        Assert.True(genre.GroupChange.CheckBoxState);
    }

    [Fact]
    public void Manganato_OrderByAndCategorySelects()
    {
        var filters = new[]
        {
            Select(0, "Order by", "Newest", "Latest", "Popularity"),
            Select(1, "Category", "All", "Action", "Adventure")
        };

        var plan = DiscoverFilterResolver.Resolve(filters);

        Assert.True(plan.CanRun);
        Assert.Equal(2, plan.Changes.Count);
        Assert.Equal(1, plan.Changes.Single(c => c.Position == 0).SelectState);
        Assert.Equal(1, plan.Changes.Single(c => c.Position == 1).SelectState);
    }

    [Fact]
    public void WeebCentral_SortAdultAndTagGroup()
    {
        var filters = new[]
        {
            Select(0, "Sort", "Popular", "Latest Updates"),
            Select(1, "Adult Content", "False", "True"),
            Group(2, "Tags", CheckBox(0, "Action"), CheckBox(1, "Romance"))
        };

        var plan = DiscoverFilterResolver.Resolve(filters);

        Assert.True(plan.CanRun);
        Assert.Equal(3, plan.Changes.Count);
        Assert.Equal(1, plan.Changes.Single(c => c.Position == 0).SelectState);
        Assert.Equal(0, plan.Changes.Single(c => c.Position == 1).SelectState);
        var tag = plan.Changes.Single(c => c.Position == 2);
        Assert.Equal(0, tag.GroupChange!.Position);
        Assert.True(tag.GroupChange.CheckBoxState);
    }

    [Fact]
    public void SourceWithoutAction_IsSkipped()
    {
        var filters = new[]
        {
            Select(0, "Sort", "Latest Updates"),
            Group(1, "Genres", CheckBox(0, "Adventure"), CheckBox(1, "Romance"))
        };

        var plan = DiscoverFilterResolver.Resolve(filters);

        Assert.False(plan.CanRun);
        Assert.Contains("Action", plan.SkipReason);
    }

    [Fact]
    public void SourceWithoutLatestSort_IsSkipped()
    {
        var filters = new[]
        {
            Select(0, "Order by", "Newest", "Popularity"),
            Select(1, "Category", "Action")
        };

        var plan = DiscoverFilterResolver.Resolve(filters);

        Assert.False(plan.CanRun);
        Assert.Contains("sort", plan.SkipReason);
    }

    [Fact]
    public void MangaDex_ChapterUploadedAtSort_AndSkipsTagsModeGroupForGenre()
    {
        // Live MangaDex filter list: "Tags mode" (a group of empty selects) also matches the
        // genre/tag name pattern, so the resolver must skip it and use the "Genre" group.
        var filters = new[]
        {
            CheckBox(0, ""),
            Group(1, "Original language", CheckBox(0, "Japanese (Manga)")),
            Group(2, "Content rating", CheckBox(0, "Safe")),
            Group(3, "Status", CheckBox(0, "Ongoing")),
            Sort(4, "Sort", "Alphabetic", "Chapter uploaded at", "Rating"),
            Select(5, "Created within last", "Any", "Day"),
            Group(6, "Tags mode", Select(0, ""), Select(1, "")),
            Group(7, "Content", TriState(0, "Gore")),
            Group(8, "Genre", TriState(0, "Action"), TriState(1, "Adventure"))
        };

        var plan = DiscoverFilterResolver.Resolve(filters);

        Assert.True(plan.CanRun);
        var sort = plan.Changes.Single(c => c.Position == 4);
        Assert.Equal(1, sort.SortState!.Index);

        var genre = plan.Changes.Single(c => c.Position == 8);
        Assert.NotNull(genre.GroupChange);
        Assert.Equal(0, genre.GroupChange!.Position);
        Assert.Equal("INCLUDE", genre.GroupChange.TriState);
    }

    [Fact]
    public void TriStateTagGroup_UsesIncludeState()
    {
        var filters = new[]
        {
            Select(0, "Sort", "Best Match", "Latest Updates"),
            Group(1, "Tags", TriState(0, "Action"), TriState(1, "Adult"))
        };

        var plan = DiscoverFilterResolver.Resolve(filters);

        Assert.True(plan.CanRun);
        var genre = plan.Changes.Single(c => c.Position == 1);
        Assert.Equal("INCLUDE", genre.GroupChange!.TriState);
        Assert.Null(genre.GroupChange.CheckBoxState);
    }
}
