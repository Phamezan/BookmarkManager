using BookmarkManager.Client.Features.Bookmarks;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarkSelectionHelperTests
{
    private static List<BookmarkNodeDto> MakeItems(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new BookmarkNodeDto { Id = Guid.NewGuid(), Title = $"Item {i}", Type = NodeType.Bookmark })
            .ToList();
    }

    [Fact]
    public void ApplyShiftClick_NoAnchor_TogglesTargetOnly()
    {
        var items = MakeItems(5);
        var selected = new HashSet<Guid>();

        BookmarkSelectionHelper.ApplyShiftClick(items, selected, anchorId: null, targetId: items[2].Id);

        Assert.Equal([items[2].Id], selected);
    }

    [Fact]
    public void ApplyShiftClick_NoAnchor_TogglingAlreadySelectedTargetDeselectsIt()
    {
        var items = MakeItems(3);
        var selected = new HashSet<Guid> { items[1].Id };

        BookmarkSelectionHelper.ApplyShiftClick(items, selected, anchorId: null, targetId: items[1].Id);

        Assert.Empty(selected);
    }

    [Fact]
    public void ApplyShiftClick_WithAnchor_AddsInclusiveRangeForward()
    {
        var items = MakeItems(6);
        var selected = new HashSet<Guid>();

        BookmarkSelectionHelper.ApplyShiftClick(items, selected, anchorId: items[1].Id, targetId: items[4].Id);

        Assert.Equal(
            new[] { items[1].Id, items[2].Id, items[3].Id, items[4].Id },
            selected.OrderBy(id => items.FindIndex(i => i.Id == id)));
    }

    [Fact]
    public void ApplyShiftClick_WithAnchor_AddsInclusiveRangeBackward()
    {
        var items = MakeItems(6);
        var selected = new HashSet<Guid>();

        BookmarkSelectionHelper.ApplyShiftClick(items, selected, anchorId: items[4].Id, targetId: items[1].Id);

        Assert.Equal(
            new[] { items[1].Id, items[2].Id, items[3].Id, items[4].Id },
            selected.OrderBy(id => items.FindIndex(i => i.Id == id)));
    }

    [Fact]
    public void ApplyShiftClick_WithAnchor_IsAddOnly_DoesNotClearSelectionOutsideRange()
    {
        var items = MakeItems(6);
        var outsideRangeId = items[5].Id;
        var selected = new HashSet<Guid> { outsideRangeId };

        BookmarkSelectionHelper.ApplyShiftClick(items, selected, anchorId: items[0].Id, targetId: items[2].Id);

        Assert.Contains(outsideRangeId, selected);
        Assert.Contains(items[0].Id, selected);
        Assert.Contains(items[1].Id, selected);
        Assert.Contains(items[2].Id, selected);
    }

    [Fact]
    public void ApplyShiftClick_AnchorNoLongerInItems_FallsBackToTogglingTarget()
    {
        var items = MakeItems(4);
        var staleAnchorId = Guid.NewGuid(); // e.g. filtered out since anchor was set
        var selected = new HashSet<Guid>();

        BookmarkSelectionHelper.ApplyShiftClick(items, selected, anchorId: staleAnchorId, targetId: items[2].Id);

        Assert.Equal([items[2].Id], selected);
    }
}
