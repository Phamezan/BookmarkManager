using BookmarkManager.Client.Features.Bookmarks;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarkKeyboardNavTests
{
    [Fact]
    public void MoveFocus_EmptyList_ReturnsNegativeOne()
    {
        Assert.Equal(-1, BookmarkKeyboardNav.MoveFocus(0, currentIndex: -1, delta: 1));
        Assert.Equal(-1, BookmarkKeyboardNav.MoveFocus(0, currentIndex: 3, delta: -1));
    }

    [Fact]
    public void MoveFocus_NoCurrentFocus_ForwardStartsAtFirstItem()
    {
        Assert.Equal(0, BookmarkKeyboardNav.MoveFocus(5, currentIndex: -1, delta: 1));
    }

    [Fact]
    public void MoveFocus_NoCurrentFocus_BackwardStartsAtLastItem()
    {
        Assert.Equal(4, BookmarkKeyboardNav.MoveFocus(5, currentIndex: -1, delta: -1));
    }

    [Fact]
    public void MoveFocus_MovesForwardByDelta()
    {
        Assert.Equal(2, BookmarkKeyboardNav.MoveFocus(5, currentIndex: 1, delta: 1));
    }

    [Fact]
    public void MoveFocus_MovesBackwardByDelta()
    {
        Assert.Equal(0, BookmarkKeyboardNav.MoveFocus(5, currentIndex: 1, delta: -1));
    }

    [Fact]
    public void MoveFocus_ClampsAtLastItem_DoesNotWrap()
    {
        Assert.Equal(4, BookmarkKeyboardNav.MoveFocus(5, currentIndex: 4, delta: 1));
    }

    [Fact]
    public void MoveFocus_ClampsAtFirstItem_DoesNotWrap()
    {
        Assert.Equal(0, BookmarkKeyboardNav.MoveFocus(5, currentIndex: 0, delta: -1));
    }

    [Fact]
    public void ClampIndex_EmptyList_ReturnsNegativeOne()
    {
        Assert.Equal(-1, BookmarkKeyboardNav.ClampIndex(0, index: 2));
    }

    [Fact]
    public void ClampIndex_IndexWithinRange_Unchanged()
    {
        Assert.Equal(2, BookmarkKeyboardNav.ClampIndex(5, index: 2));
    }

    [Fact]
    public void ClampIndex_IndexBeyondEnd_ClampsToLastItem()
    {
        Assert.Equal(4, BookmarkKeyboardNav.ClampIndex(5, index: 10));
    }

    [Fact]
    public void ClampIndex_NegativeIndex_ClampsToZero()
    {
        Assert.Equal(0, BookmarkKeyboardNav.ClampIndex(5, index: -3));
    }
}
