using BookmarkManager.Client.Features.Bookmarks;

namespace BookmarkManager.Client.ComponentTests;

public sealed class MarqueeSelectionHelperTests
{
    [Fact]
    public void Apply_Replace_ClearsPreviousSelection_AddsHits()
    {
        var preExisting = Guid.NewGuid();
        var selected = new HashSet<Guid> { preExisting };
        var hitA = Guid.NewGuid();
        var hitB = Guid.NewGuid();

        MarqueeSelectionHelper.Apply(selected, [hitA, hitB], additive: false);

        Assert.Equal(new HashSet<Guid> { hitA, hitB }, selected);
        Assert.DoesNotContain(preExisting, selected);
    }

    [Fact]
    public void Apply_Additive_UnionsHitsWithoutRemovingExisting()
    {
        var preExisting = Guid.NewGuid();
        var selected = new HashSet<Guid> { preExisting };
        var hitA = Guid.NewGuid();
        var hitB = Guid.NewGuid();

        MarqueeSelectionHelper.Apply(selected, [hitA, hitB], additive: true);

        Assert.Equal(new HashSet<Guid> { preExisting, hitA, hitB }, selected);
    }

    [Fact]
    public void Apply_EmptyHits_Replace_ResultsInEmptySelection()
    {
        var selected = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() };

        MarqueeSelectionHelper.Apply(selected, [], additive: false);

        Assert.Empty(selected);
    }

    [Fact]
    public void Apply_EmptyHits_Additive_LeavesSelectionUnchanged()
    {
        var existingA = Guid.NewGuid();
        var existingB = Guid.NewGuid();
        var selected = new HashSet<Guid> { existingA, existingB };

        MarqueeSelectionHelper.Apply(selected, [], additive: true);

        Assert.Equal(new HashSet<Guid> { existingA, existingB }, selected);
    }

    [Fact]
    public void Apply_ReturnsLastHitInEnumerationOrder_AsAnchor()
    {
        var selected = new HashSet<Guid>();
        var hitA = Guid.NewGuid();
        var hitB = Guid.NewGuid();
        var hitC = Guid.NewGuid();

        var anchor = MarqueeSelectionHelper.Apply(selected, [hitA, hitB, hitC], additive: false);

        Assert.Equal(hitC, anchor);
    }

    [Fact]
    public void Apply_EmptyHits_ReturnsNullAnchor()
    {
        var selected = new HashSet<Guid>();

        var anchor = MarqueeSelectionHelper.Apply(selected, [], additive: false);

        Assert.Null(anchor);
    }

    [Fact]
    public void Apply_Additive_DuplicateHitAlreadySelected_StillReturnedAsAnchor()
    {
        var existing = Guid.NewGuid();
        var selected = new HashSet<Guid> { existing };

        var anchor = MarqueeSelectionHelper.Apply(selected, [existing], additive: true);

        Assert.Equal(existing, anchor);
        Assert.Contains(existing, selected);
    }
}
