using BookmarkManager.Client.Features.Bookmarks;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarkListOrderingTests
{
    private static BookmarkNodeDto Node(string title, NodeType type, bool favorite = false, DateTime? updatedAt = null)
    {
        return new BookmarkNodeDto
        {
            Id = Guid.NewGuid(),
            Title = title,
            Type = type,
            UpdatedAt = updatedAt ?? DateTime.UtcNow,
            Metadata = new BookmarkMetadataDto { IsFavorite = favorite }
        };
    }

    [Fact]
    public void ApplySort_FoldersAlwaysBeforeBookmarks_RegardlessOfTitleOrFavorite()
    {
        var zFolder = Node("Z Folder", NodeType.Folder);
        var aBookmarkFavorite = Node("A Bookmark", NodeType.Bookmark, favorite: true);
        var items = new List<BookmarkNodeDto> { aBookmarkFavorite, zFolder };

        var result = BookmarkListOrdering.ApplySort(items, "UpdatedDesc").ToList();

        Assert.Equal(zFolder.Id, result[0].Id);
        Assert.Equal(aBookmarkFavorite.Id, result[1].Id);
    }

    [Fact]
    public void ApplySort_WithinFolders_FavoritesFirstThenTitle()
    {
        var folderB = Node("B Folder", NodeType.Folder);
        var folderAFavorite = Node("A Folder", NodeType.Folder, favorite: true);
        var bookmark = Node("Any Bookmark", NodeType.Bookmark);
        var items = new List<BookmarkNodeDto> { folderB, bookmark, folderAFavorite };

        var result = BookmarkListOrdering.ApplySort(items, "Default").ToList();

        Assert.Equal(folderAFavorite.Id, result[0].Id);
        Assert.Equal(folderB.Id, result[1].Id);
        Assert.Equal(bookmark.Id, result[2].Id);
    }

    [Fact]
    public void ApplySort_WithinBookmarks_FavoritesFirstThenTitle()
    {
        var folder = Node("Only Folder", NodeType.Folder);
        var bookmarkB = Node("B Bookmark", NodeType.Bookmark);
        var bookmarkAFavorite = Node("A Bookmark", NodeType.Bookmark, favorite: true);
        var items = new List<BookmarkNodeDto> { bookmarkB, folder, bookmarkAFavorite };

        var result = BookmarkListOrdering.ApplySort(items, "Default").ToList();

        Assert.Equal(folder.Id, result[0].Id);
        Assert.Equal(bookmarkAFavorite.Id, result[1].Id);
        Assert.Equal(bookmarkB.Id, result[2].Id);
    }

    [Fact]
    public void ApplySort_TitleDesc_SortsWithinTypeByTitleDescending()
    {
        var folderA = Node("A Folder", NodeType.Folder);
        var folderZ = Node("Z Folder", NodeType.Folder);
        var bookmarkA = Node("A Bookmark", NodeType.Bookmark);
        var bookmarkZ = Node("Z Bookmark", NodeType.Bookmark);
        var items = new List<BookmarkNodeDto> { folderA, bookmarkA, folderZ, bookmarkZ };

        var result = BookmarkListOrdering.ApplySort(items, "TitleDesc").ToList();

        Assert.Equal(new[] { folderZ.Id, folderA.Id, bookmarkZ.Id, bookmarkA.Id }, result.Select(i => i.Id));
    }

    [Fact]
    public void ApplySort_UpdatedAsc_SortsWithinTypeByUpdatedAtAscending()
    {
        var now = DateTime.UtcNow;
        var folderOld = Node("Folder Old", NodeType.Folder, updatedAt: now.AddDays(-2));
        var folderNew = Node("Folder New", NodeType.Folder, updatedAt: now);
        var bookmarkOld = Node("Bookmark Old", NodeType.Bookmark, updatedAt: now.AddDays(-2));
        var bookmarkNew = Node("Bookmark New", NodeType.Bookmark, updatedAt: now);
        var items = new List<BookmarkNodeDto> { folderNew, bookmarkNew, folderOld, bookmarkOld };

        var result = BookmarkListOrdering.ApplySort(items, "UpdatedAsc").ToList();

        Assert.Equal(new[] { folderOld.Id, folderNew.Id, bookmarkOld.Id, bookmarkNew.Id }, result.Select(i => i.Id));
    }

    [Fact]
    public void ApplySort_UpdatedDesc_SortsWithinTypeByUpdatedAtDescending()
    {
        var now = DateTime.UtcNow;
        var folderOld = Node("Folder Old", NodeType.Folder, updatedAt: now.AddDays(-2));
        var folderNew = Node("Folder New", NodeType.Folder, updatedAt: now);
        var bookmarkOld = Node("Bookmark Old", NodeType.Bookmark, updatedAt: now.AddDays(-2));
        var bookmarkNew = Node("Bookmark New", NodeType.Bookmark, updatedAt: now);
        var items = new List<BookmarkNodeDto> { folderOld, bookmarkOld, folderNew, bookmarkNew };

        var result = BookmarkListOrdering.ApplySort(items, "UpdatedDesc").ToList();

        Assert.Equal(new[] { folderNew.Id, folderOld.Id, bookmarkNew.Id, bookmarkOld.Id }, result.Select(i => i.Id));
    }
}
