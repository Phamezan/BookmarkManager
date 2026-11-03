using BookmarkManager.Api.Services.Suwayomi;
using Xunit;

namespace BookmarkManager.UnitTests.Suwayomi;

public sealed class SuwayomiLibrarySyncPlannerTests
{
    private const string BaseUrl = "http://suwayomi.local:4567";

    private static readonly Guid BookmarkA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BookmarkB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BookmarkC = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static SuwayomiLibraryManga Manga(
        int id, string title = "Series", double? read = null, double? highest = null, string? realUrl = null,
        int? resumeSourceOrder = null)
        => new(title, read, highest, id, realUrl, ResumeSourceOrder: resumeSourceOrder);

    private static SuwayomiLibrarySyncBookmark Bookmark(
        Guid id, int? mangaId, string title = "Series", string? url = null, int? progress = null,
        bool deleted = false, bool removed = false)
        => new(id, title, url, mangaId, progress, deleted, removed);

    private static SuwayomiLibrarySyncPlan Plan(
        IReadOnlyList<SuwayomiLibraryManga> library,
        IReadOnlyList<SuwayomiLibrarySyncBookmark> bookmarks,
        IReadOnlySet<int> previouslyUnlinked,
        bool syncFolderAvailable = true,
        int maxRemovalsPerCycle = 5,
        int maxLibraryRemovalsPerCycle = 10)
        => SuwayomiLibrarySyncPlanner.Plan(
            library, bookmarks, previouslyUnlinked, syncFolderAvailable, BaseUrl, maxRemovalsPerCycle,
            maxLibraryRemovalsPerCycle);

    [Fact]
    public void ReadGaps_ReturnsOnlySeriesWithUnreadChaptersBeforeTheLatestRead()
    {
        var gap = Manga(1) with { FirstUnreadSourceOrder = 105, LatestReadSourceOrder = 122 };
        var library = new[]
        {
            gap,
            Manga(2) with { FirstUnreadSourceOrder = 123, LatestReadSourceOrder = 122 },
            Manga(3) with { FirstUnreadSourceOrder = null, LatestReadSourceOrder = 50 },
            Manga(4) with { FirstUnreadSourceOrder = 1, LatestReadSourceOrder = null }
        };

        Assert.Equal(new[] { gap }, SuwayomiLibrarySyncPlanner.ReadGaps(library));
    }

    [Fact]
    public void Create_HappensOnlyOnTheSecondCycle()
    {
        var library = new[] { Manga(5, "Nano Machine", read: 12) };

        var first = Plan(library, [], new HashSet<int>());

        Assert.Empty(first.Creations);
        Assert.Single(first.Unlinked);
        Assert.Contains(5, first.Unlinked);

        var second = Plan(library, [], first.Unlinked);

        var creation = Assert.Single(second.Creations);
        Assert.Equal(5, creation.MangaId);
        Assert.Equal("Nano Machine", creation.Title);
        Assert.Equal($"{BaseUrl}/manga/5", creation.Url);
        Assert.Equal(12, creation.CurrentProgress);
    }

    [Fact]
    public void Create_UsesResumeChapterUrl()
    {
        var library = new[] { Manga(5, resumeSourceOrder: 0) };

        var plan = Plan(library, [], new HashSet<int> { 5 });

        Assert.Equal($"{BaseUrl}/manga/5/chapter/0", Assert.Single(plan.Creations).Url);
    }

    [Fact]
    public void LinkedBookmark_UpdatesSeriesUrlToResumeChapter()
    {
        var library = new[] { Manga(5, resumeSourceOrder: 13) };
        var bookmarks = new[] { Bookmark(BookmarkA, 5, url: $"{BaseUrl}/manga/5") };

        var plan = Plan(library, bookmarks, new HashSet<int>());

        var update = Assert.Single(plan.UrlUpdates);
        Assert.Equal(BookmarkA, update.BookmarkId);
        Assert.Equal($"{BaseUrl}/manga/5/chapter/13", update.Url);
    }

    [Fact]
    public void TrialEraBookmark_IsLinkedAndGetsResumeChapterInSameCycle()
    {
        var library = new[] { Manga(5, resumeSourceOrder: 13) };
        var bookmarks = new[] { Bookmark(BookmarkA, null, url: $"{BaseUrl}/manga/5") };

        var plan = Plan(library, bookmarks, new HashSet<int> { 5 });

        Assert.Single(plan.Links);
        Assert.Empty(plan.Creations);
        Assert.Equal($"{BaseUrl}/manga/5/chapter/13", Assert.Single(plan.UrlUpdates).Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://suwayomi.local:4567/manga/5/chapter/12")]
    public void LinkedBookmark_UpdatesNullOrPreviousChapterUrl(string? url)
    {
        var plan = Plan([Manga(5, resumeSourceOrder: 13)], [Bookmark(BookmarkA, 5, url: url)], new HashSet<int>());

        Assert.Equal($"{BaseUrl}/manga/5/chapter/13", Assert.Single(plan.UrlUpdates).Url);
    }

    [Fact]
    public void LinkedBookmark_AlreadyAtResumeChapter_HasNoUrlUpdate()
    {
        var plan = Plan([Manga(5, resumeSourceOrder: 13)],
            [Bookmark(BookmarkA, 5, url: $"{BaseUrl}/manga/5/chapter/13")], new HashSet<int>());

        Assert.Empty(plan.UrlUpdates);
    }

    [Fact]
    public void CaughtUpManga_UsesLatestReadResumeChapter()
    {
        var plan = Plan([Manga(5, read: 12.5, highest: 12.5, resumeSourceOrder: 27)],
            [Bookmark(BookmarkA, 5, url: $"{BaseUrl}/manga/5/chapter/26")], new HashSet<int>());

        Assert.Equal($"{BaseUrl}/manga/5/chapter/27", Assert.Single(plan.UrlUpdates).Url);
    }

    [Fact]
    public void NoResumeChapter_KeepsSeriesUrl()
    {
        var plan = Plan([Manga(5)], [Bookmark(BookmarkA, 5, url: $"{BaseUrl}/manga/5")], new HashSet<int>());

        Assert.Empty(plan.UrlUpdates);
    }

    [Fact]
    public void NoResumeChapter_RevertsPreviousChapterUrlToSeriesUrl()
    {
        var plan = Plan([Manga(5)], [Bookmark(BookmarkA, 5, url: $"{BaseUrl}/manga/5/chapter/12")], new HashSet<int>());

        Assert.Equal($"{BaseUrl}/manga/5", Assert.Single(plan.UrlUpdates).Url);
    }

    [Theory]
    [InlineData("https://example.com/manga/5")]
    [InlineData("http://suwayomi.local:4567/manga-custom/5")]
    [InlineData("http://suwayomi.local:4567/Manga/5")]
    public void HandSetUrl_IsPreserved(string url)
    {
        var plan = Plan([Manga(5, resumeSourceOrder: 13)], [Bookmark(BookmarkA, 5, url: url)], new HashSet<int>());

        Assert.Empty(plan.UrlUpdates);
    }

    [Fact]
    public void Relink_UsesNewMangaResumeChapterUrl()
    {
        var plan = Plan([Manga(6, resumeSourceOrder: 13)],
            [Bookmark(BookmarkA, 5, url: $"{BaseUrl}/manga/5/chapter/9")], new HashSet<int>());

        Assert.Single(plan.Relinks);
        var update = Assert.Single(plan.UrlUpdates);
        Assert.Equal(BookmarkA, update.BookmarkId);
        Assert.Equal($"{BaseUrl}/manga/6/chapter/13", update.Url);
    }

    [Fact]
    public void Relink_PreservesHandSetUrl()
    {
        var plan = Plan([Manga(6, resumeSourceOrder: 13)],
            [Bookmark(BookmarkA, 5, url: "https://example.com/custom")], new HashSet<int>());

        Assert.Single(plan.Relinks);
        Assert.Empty(plan.UrlUpdates);
    }

    [Fact]
    public void DeletedBookmark_HasNoUrlUpdate()
    {
        var plan = Plan([Manga(5, resumeSourceOrder: 13)],
            [Bookmark(BookmarkA, 5, url: $"{BaseUrl}/manga/5", deleted: true)], new HashSet<int>());

        Assert.Empty(plan.UrlUpdates);
    }

    [Fact]
    public void ReaderUrl_TrimsTrailingBaseUrlSlash()
    {
        var plan = SuwayomiLibrarySyncPlanner.Plan([Manga(5, resumeSourceOrder: 13)], [], new HashSet<int> { 5 },
            true, $"{BaseUrl}/", 5, 10);

        Assert.Equal($"{BaseUrl}/manga/5/chapter/13", Assert.Single(plan.Creations).Url);
    }

    [Fact]
    public void Create_SkippedWhenFolderUnavailable()
    {
        var library = new[] { Manga(5) };

        var plan = Plan(library, [], new HashSet<int> { 5 }, syncFolderAvailable: false);

        Assert.Empty(plan.Creations);
    }

    [Fact]
    public void ExistingUrlBookmark_IsLinkedInsteadOfCreated()
    {
        var library = new[] { Manga(5, realUrl: "https://asurascans.com/nano-machine") };
        var bookmarks = new[] { Bookmark(BookmarkA, mangaId: null, url: $"{BaseUrl}/manga/5") };

        var plan = Plan(library, bookmarks, new HashSet<int> { 5 });

        Assert.Empty(plan.Creations);
        var link = Assert.Single(plan.Links);
        Assert.Equal(BookmarkA, link.BookmarkId);
        Assert.Equal(5, link.MangaId);
        Assert.Equal("https://asurascans.com/nano-machine", link.SourceUrl);
    }

    [Fact]
    public void SoftDeletedLink_BlocksCreationAndRemoval()
    {
        var library = new[] { Manga(5) };
        var bookmarks = new[] { Bookmark(BookmarkA, mangaId: 5, url: $"{BaseUrl}/manga/5", deleted: true) };

        var plan = Plan(library, bookmarks, new HashSet<int> { 5 });

        Assert.Empty(plan.Creations);
        Assert.Empty(plan.Links);
        Assert.Empty(plan.Removals);
        Assert.Empty(plan.Unlinked);
        Assert.Equal(0, plan.RemovalsSuppressed);
    }

    [Fact]
    public void RemovedManga_RemovesAllLinkingBookmarks()
    {
        var library = new[] { Manga(9) };
        var bookmarks = new[]
        {
            Bookmark(BookmarkA, mangaId: 5),
            Bookmark(BookmarkB, mangaId: 5),
            Bookmark(BookmarkC, mangaId: 9)
        };

        var plan = Plan(library, bookmarks, new HashSet<int>());

        Assert.Equal(2, plan.Removals.Count);
        Assert.Contains(BookmarkA, plan.Removals);
        Assert.Contains(BookmarkB, plan.Removals);
        Assert.DoesNotContain(BookmarkC, plan.Removals);
    }

    [Fact]
    public void EmptyLibrary_SuppressesRemovals()
    {
        var bookmarks = new[] { Bookmark(BookmarkA, mangaId: 5) };

        var plan = Plan([], bookmarks, new HashSet<int>());

        Assert.Empty(plan.Removals);
        Assert.True(plan.LibraryEmpty);
        Assert.Equal(1, plan.RemovalsSuppressed);
    }

    [Fact]
    public void OverCapRemovals_AreSuppressed()
    {
        var library = new[] { Manga(9) };
        var bookmarks = new[]
        {
            Bookmark(BookmarkA, mangaId: 5),
            Bookmark(BookmarkB, mangaId: 6),
            Bookmark(BookmarkC, mangaId: 7)
        };

        var plan = Plan(library, bookmarks, new HashSet<int>(), maxRemovalsPerCycle: 2);

        Assert.Empty(plan.Removals);
        Assert.False(plan.LibraryEmpty);
        Assert.Equal(3, plan.RemovalsSuppressed);
    }

    [Fact]
    public void Progress_MovesForwardOnly()
    {
        var library = new[] { Manga(5, read: 12.9) };
        var bookmarks = new[]
        {
            Bookmark(BookmarkA, mangaId: 5, progress: 10),
            Bookmark(BookmarkB, mangaId: 5, progress: 20),
            Bookmark(BookmarkC, mangaId: 5, progress: null)
        };

        var plan = Plan(library, bookmarks, new HashSet<int>());

        Assert.Equal(2, plan.ProgressUpdates.Count);
        Assert.Contains(plan.ProgressUpdates, p => p.BookmarkId == BookmarkA && p.Progress == 12);
        Assert.Contains(plan.ProgressUpdates, p => p.BookmarkId == BookmarkC && p.Progress == 12);
        Assert.DoesNotContain(plan.ProgressUpdates, p => p.BookmarkId == BookmarkB);
    }

    [Fact]
    public void MigratedSeries_IsRelinkedInsteadOfDeletedAndRecreated()
    {
        var library = new[] { Manga(6, "Nano Machine", read: 12, realUrl: "https://asurascans.com/nano") };
        var bookmarks = new[] { Bookmark(BookmarkA, mangaId: 5, title: "Nano Machine", progress: 8) };

        var plan = Plan(library, bookmarks, new HashSet<int>());

        var relink = Assert.Single(plan.Relinks);
        Assert.Equal(BookmarkA, relink.BookmarkId);
        Assert.Equal(6, relink.MangaId);
        Assert.Equal("https://asurascans.com/nano", relink.SourceUrl);
        Assert.Empty(plan.Creations);
        Assert.Empty(plan.Removals);
        Assert.Contains(plan.ProgressUpdates, p => p.BookmarkId == BookmarkA && p.Progress == 12);
    }

    [Fact]
    public void AmbiguousRelinkMatch_DoesNotRelink()
    {
        var library = new[] { Manga(6, "Nano Machine"), Manga(7, "Nano Machine") };
        var bookmarks = new[] { Bookmark(BookmarkA, mangaId: 5, title: "Nano Machine") };

        var plan = Plan(library, bookmarks, new HashSet<int>());

        Assert.Empty(plan.Relinks);
        Assert.Contains(BookmarkA, plan.Removals);
    }

    [Fact]
    public void NonMatchingTitle_StillRemoves()
    {
        var library = new[] { Manga(6, "Some Other Series") };
        var bookmarks = new[] { Bookmark(BookmarkA, mangaId: 5, title: "Nano Machine") };

        var plan = Plan(library, bookmarks, new HashSet<int>());

        Assert.Empty(plan.Relinks);
        Assert.Contains(BookmarkA, plan.Removals);
    }

    [Fact]
    public void Progress_SkippedForDeletedOrUnlinkedBookmarks()
    {
        var library = new[] { Manga(5, read: 12) };
        var bookmarks = new[]
        {
            Bookmark(BookmarkA, mangaId: 5, progress: 10, deleted: true),
            Bookmark(BookmarkB, mangaId: null, progress: 1)
        };

        var plan = Plan(library, bookmarks, new HashSet<int>());

        Assert.Empty(plan.ProgressUpdates);
    }

    [Fact]
    public void DeletedBookmark_RemovesSeriesAndStampsIt()
    {
        var library = new[] { Manga(5, "Nano Machine") };
        var bookmarks = new[] { Bookmark(BookmarkA, mangaId: 5, deleted: true) };

        var plan = Plan(library, bookmarks, new HashSet<int>());

        var removal = Assert.Single(plan.LibraryRemovals);
        Assert.Equal(5, removal.MangaId);
        Assert.Equal(BookmarkA, Assert.Single(removal.BookmarkIds));
        Assert.Empty(plan.LibraryRestores);
        Assert.Empty(plan.Removals);
    }

    [Fact]
    public void DeletedBookmark_WithLiveBookmarkOnSameSeries_DoesNotRemove()
    {
        var library = new[] { Manga(5, "Nano Machine") };
        var bookmarks = new[]
        {
            Bookmark(BookmarkA, mangaId: 5, deleted: true),
            Bookmark(BookmarkB, mangaId: 5)
        };

        var plan = Plan(library, bookmarks, new HashSet<int>());

        Assert.Empty(plan.LibraryRemovals);
        Assert.Empty(plan.LibraryRestores);
    }

    [Fact]
    public void LibraryRemovals_AreThrottledAtTheCapInMangaIdOrder()
    {
        var library = new[] { Manga(5), Manga(6) };
        var bookmarks = new[]
        {
            Bookmark(BookmarkB, mangaId: 6, deleted: true),
            Bookmark(BookmarkA, mangaId: 5, deleted: true)
        };

        var plan = Plan(library, bookmarks, new HashSet<int>(), maxLibraryRemovalsPerCycle: 1);

        var removal = Assert.Single(plan.LibraryRemovals);
        Assert.Equal(5, removal.MangaId);
        Assert.Equal(1, plan.LibraryRemovalsSuppressed);
    }

    [Fact]
    public void EmptyLibrary_ProducesNoLibraryRemovals()
    {
        var bookmarks = new[] { Bookmark(BookmarkA, mangaId: 5, deleted: true) };

        var plan = Plan([], bookmarks, new HashSet<int>());

        Assert.Empty(plan.LibraryRemovals);
        Assert.True(plan.LibraryEmpty);
    }

    [Fact]
    public void RestoredBookmark_IsRestoredAndNotARemovalCandidate()
    {
        var library = new[] { Manga(9, "Some Other Series") };
        var bookmarks = new[] { Bookmark(BookmarkA, mangaId: 5, removed: true) };

        var plan = Plan(library, bookmarks, new HashSet<int>());

        var restore = Assert.Single(plan.LibraryRestores);
        Assert.Equal(BookmarkA, restore.BookmarkId);
        Assert.Equal(5, restore.MangaId);
        Assert.False(restore.MangaInLibrary);
        Assert.Empty(plan.Removals);
    }

    [Fact]
    public void MarkedBookmark_WithSeriesAlreadyInLibrary_ClearsMarkerOnly()
    {
        var library = new[] { Manga(5, "Nano Machine") };
        var bookmarks = new[] { Bookmark(BookmarkA, mangaId: 5, removed: true) };

        var plan = Plan(library, bookmarks, new HashSet<int>());

        var restore = Assert.Single(plan.LibraryRestores);
        Assert.True(restore.MangaInLibrary);
        Assert.Empty(plan.LibraryRemovals);
        Assert.Empty(plan.Removals);
    }

    [Fact]
    public void AlreadyStampedDeletedBookmark_DoesNothing()
    {
        var library = new[] { Manga(5, "Nano Machine") };
        var bookmarks = new[] { Bookmark(BookmarkA, mangaId: 5, deleted: true, removed: true) };

        var plan = Plan(library, bookmarks, new HashSet<int>());

        Assert.Empty(plan.LibraryRemovals);
        Assert.Empty(plan.LibraryRestores);
        Assert.Empty(plan.Removals);
    }
}
