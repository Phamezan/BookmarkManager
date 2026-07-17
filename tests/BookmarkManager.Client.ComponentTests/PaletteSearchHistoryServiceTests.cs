using BookmarkManager.Client.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class PaletteSearchHistoryServiceTests
{
    [Fact]
    public void PushFront_NewQuery_InsertsAtFront()
    {
        var result = PaletteSearchHistoryService.PushFront(["older", "oldest"], "newest");
        Assert.Equal(["newest", "older", "oldest"], result);
    }

    [Fact]
    public void PushFront_Duplicate_MovesToFront()
    {
        var result = PaletteSearchHistoryService.PushFront(["a", "b", "c"], "b");
        Assert.Equal(["b", "a", "c"], result);
    }

    [Fact]
    public void PushFront_DuplicateIgnoreCase_MovesToFront()
    {
        var result = PaletteSearchHistoryService.PushFront(["Naruto", "bleach"], "naruto");
        Assert.Equal(["naruto", "bleach"], result);
    }

    [Fact]
    public void PushFront_OverMax_TrimsOldest()
    {
        var existing = Enumerable.Range(0, 20).Select(i => $"q{i}").ToList();
        var result = PaletteSearchHistoryService.PushFront(existing, "fresh", maxEntries: 20);
        Assert.Equal(20, result.Count);
        Assert.Equal("fresh", result[0]);
        Assert.DoesNotContain("q19", result);
    }
}
