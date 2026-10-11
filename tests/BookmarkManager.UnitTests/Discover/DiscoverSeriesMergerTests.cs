using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Contracts;

namespace BookmarkManager.UnitTests.Discover;

public sealed class DiscoverSeriesMergerTests
{
    private static readonly DateTime Base = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    private static DiscoverChapterDto Chapter(double number, DateTime uploadedAt, int mangaId = 1, bool isNew = false)
        => new()
        {
            Number = number,
            Name = $"Chapter {number}",
            UploadedAt = uploadedAt,
            MangaId = mangaId,
            SourceOrder = (int)number,
            IsNew = isNew
        };

    private static DiscoverItemDto Item(
        string key,
        string title,
        string type,
        int coverMangaId,
        int seriesMangaId,
        string[] sources,
        DiscoverChapterDto[] chapters,
        DiscoverProgressDto? progress = null)
        => new()
        {
            TitleKey = key,
            Title = title,
            Type = type,
            CoverMangaId = coverMangaId,
            SeriesMangaId = seriesMangaId,
            Sources = [.. sources],
            Chapters = [.. chapters],
            Progress = progress
        };

    [Fact]
    public void SameAnilistId_MergesSourcesChaptersAndProgress()
    {
        var older = Item(
            "there's no such thing as a bad lady", "There's No Such Thing as a Bad Lady", "Manhwa", 10, 10,
            ["Manganato", "Weeb Central"],
            [Chapter(5, Base.AddDays(-6)), Chapter(4, Base.AddDays(-8))],
            new DiscoverProgressDto { Read = 2, Latest = 5 });
        var newer = Item(
            "noble lady reformation guide", "Noble Lady Reformation Guide", "Unknown", 20, 20,
            ["Manganato", "Asura Scans"],
            [Chapter(5, Base.AddDays(-1)), Chapter(3, Base.AddDays(-3))],
            new DiscoverProgressDto { Read = 4, Latest = 5 });
        var map = new Dictionary<string, int>
        {
            ["there's no such thing as a bad lady"] = 206766,
            ["noble lady reformation guide"] = 206766
        };

        var merged = DiscoverSeriesMerger.Merge([older, newer], map);

        var card = Assert.Single(merged);
        Assert.Equal("Noble Lady Reformation Guide", card.Title);
        Assert.Equal("noble lady reformation guide", card.TitleKey);
        Assert.Equal(20, card.CoverMangaId);
        Assert.Equal(20, card.SeriesMangaId);
        Assert.Equal(new[] { "Manganato", "Asura Scans", "Weeb Central" }, card.Sources);
        Assert.Equal(new[] { 5d, 4d, 3d }, card.Chapters.Select(c => c.Number));
        Assert.Equal(4, card.Progress!.Read);
        Assert.Equal(5, card.Progress.Latest);
    }

    [Fact]
    public void SameAnilistId_ChapterDedupeKeepsEarliestUploadDate()
    {
        var first = Item(
            "a", "A", "Manhwa", 1, 1, ["S1"],
            [Chapter(5, Base.AddDays(-2), isNew: true)]);
        var second = Item(
            "b", "B", "Manhwa", 2, 2, ["S2"],
            [Chapter(5, Base.AddDays(-9), isNew: false)]);
        var map = new Dictionary<string, int> { ["a"] = 7, ["b"] = 7 };

        var card = Assert.Single(DiscoverSeriesMerger.Merge([first, second], map));

        var chapter = Assert.Single(card.Chapters);
        Assert.Equal(5, chapter.Number);
        Assert.Equal(Base.AddDays(-9), chapter.UploadedAt);
        Assert.False(chapter.IsNew);
    }

    [Fact]
    public void PrimaryIsMostRecentChapter_NotFirstInList()
    {
        var older = Item(
            "a", "Old Title", "Manhwa", 1, 1, ["S1"],
            [Chapter(1, Base.AddDays(-10))]);
        var newer = Item(
            "b", "New Title", "Manhwa", 2, 2, ["S2"],
            [Chapter(1, Base.AddDays(-1))]);
        var map = new Dictionary<string, int> { ["a"] = 7, ["b"] = 7 };

        var card = Assert.Single(DiscoverSeriesMerger.Merge([older, newer], map));

        Assert.Equal("New Title", card.Title);
        Assert.Equal(2, card.SeriesMangaId);
        Assert.Equal(2, card.CoverMangaId);
    }

    [Fact]
    public void DifferentAnilistIds_DoNotMerge()
    {
        var a = Item("a", "A", "Manhwa", 1, 1, ["S1"], [Chapter(1, Base.AddDays(-1))]);
        var b = Item("b", "B", "Manhwa", 2, 2, ["S2"], [Chapter(1, Base.AddDays(-2))]);
        var map = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };

        var result = DiscoverSeriesMerger.Merge([a, b], map);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "A", "B" }, result.Select(i => i.Title));
    }

    [Fact]
    public void MissingAnilistId_NeverMerges()
    {
        var withId = Item("a", "A", "Manhwa", 1, 1, ["S1"], [Chapter(1, Base.AddDays(-1))]);
        var withoutId = Item("b", "B", "Manhwa", 2, 2, ["S2"], [Chapter(1, Base.AddDays(-2))]);
        var map = new Dictionary<string, int> { ["a"] = 7 };

        var result = DiscoverSeriesMerger.Merge([withId, withoutId], map);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NeitherHasAnilistId_StaysSeparate()
    {
        var a = Item("a", "A", "Manhwa", 1, 1, ["S1"], [Chapter(1, Base.AddDays(-1))]);
        var b = Item("b", "B", "Manhwa", 2, 2, ["S2"], [Chapter(1, Base.AddDays(-2))]);

        var result = DiscoverSeriesMerger.Merge([a, b], new Dictionary<string, int>());

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void TypeFallsBackToFirstNonUnknown()
    {
        var primary = Item("a", "A", "Unknown", 1, 1, ["S1"], [Chapter(1, Base.AddDays(-1))]);
        var other = Item("b", "B", "Manga", 2, 2, ["S2"], [Chapter(1, Base.AddDays(-9))]);
        var map = new Dictionary<string, int> { ["a"] = 7, ["b"] = 7 };

        var card = Assert.Single(DiscoverSeriesMerger.Merge([primary, other], map));

        Assert.Equal("Manga", card.Type);
    }

    [Fact]
    public void TypeFallbackKeepsUnknownWhenAllUnknown()
    {
        var primary = Item("a", "A", "Unknown", 1, 1, ["S1"], [Chapter(1, Base.AddDays(-1))]);
        var other = Item("b", "B", "Unknown", 2, 2, ["S2"], [Chapter(1, Base.AddDays(-9))]);
        var map = new Dictionary<string, int> { ["a"] = 7, ["b"] = 7 };

        var card = Assert.Single(DiscoverSeriesMerger.Merge([primary, other], map));

        Assert.Equal("Unknown", card.Type);
    }

    [Fact]
    public void MergedListOrderedByPrimaryLatestChapter()
    {
        var groupOne = Item("a", "Recent Series", "Manhwa", 1, 1, ["S1"], [Chapter(1, Base.AddDays(-1))]);
        var groupTwo = Item("b", "Older Series", "Manhwa", 2, 2, ["S2"], [Chapter(1, Base.AddDays(-5))]);
        var map = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };

        var result = DiscoverSeriesMerger.Merge([groupTwo, groupOne], map);

        Assert.Equal(new[] { "Recent Series", "Older Series" }, result.Select(i => i.Title));
    }
}
