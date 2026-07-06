using BookmarkManager.Client.Pages;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class AnimeCalendarTests
{
    [Fact]
    public async Task NoFoldersSelected_ShowsChooseFoldersEmptyState()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeAnimeBookmarkService());

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AnimeCalendar>(1);
            builder.CloseComponent();
        });

        page.WaitForAssertion(() =>
            Assert.Contains("Choose folders to build your calendar", page.Markup));
    }

    [Fact]
    public async Task SelectingFolder_ShowsMatchButton_WhenScheduleHasUnmatchedBookmarks()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var folderId = Guid.NewGuid();
        var unmatchedBookmark = new BookmarkNodeDto { Id = Guid.NewGuid(), Title = "Naruto" };
        var fakeService = new FakeAnimeBookmarkService
        {
            FolderTree = [new FolderTreeNodeDto { Id = folderId, Title = "Anime" }],
            ScheduleResponse = new AnimeCalendarScheduleResponse
            {
                Entries = [],
                UnmatchedBookmarks = [unmatchedBookmark]
            }
        };
        context.Services.AddSingleton<IBookmarkService>(fakeService);

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AnimeCalendar>(1);
            builder.CloseComponent();
        });

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".rec-folder-chip")));
        var folderChip = page.FindAll(".rec-folder-chip").First(b => b.TextContent.Trim() == "Anime");
        folderChip.Click();

        // The cluttered per-title banner is gone; unmatched bookmarks now surface only as a compact
        // "Match N new" action that triggers auto-match.
        page.WaitForAssertion(() =>
            Assert.Contains("Match 1 new", page.Markup));
    }

    [Fact]
    public async Task SelectingFolder_WithEntries_WeekViewRendersEpisode()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var folderId = Guid.NewGuid();
        var fakeService = new FakeAnimeBookmarkService
        {
            FolderTree = [new FolderTreeNodeDto { Id = folderId, Title = "Anime" }],
            ScheduleResponse = new AnimeCalendarScheduleResponse
            {
                Entries =
                [
                    new AnimeCalendarEntryDto
                    {
                        BookmarkId = Guid.NewGuid(),
                        Title = "Mushoku Tensei",
                        EpisodeNumber = 5,
                        AiringAtUtc = DateTimeOffset.Now
                    }
                ],
                AiringCount = 1
            }
        };
        context.Services.AddSingleton<IBookmarkService>(fakeService);

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AnimeCalendar>(1);
            builder.CloseComponent();
        });

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".rec-folder-chip")));
        page.FindAll(".rec-folder-chip").First(b => b.TextContent.Trim() == "Anime").Click();

        // Week (default view) groups the week's episodes into a roadmap timeline card.
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Mushoku Tensei", page.Markup);
            Assert.Contains("Ep 5", page.Markup);
        });
    }

    [Fact]
    public async Task SwitchingToMonthView_ShowsEpisodeCountBadge()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var folderId = Guid.NewGuid();
        var fakeService = new FakeAnimeBookmarkService
        {
            FolderTree = [new FolderTreeNodeDto { Id = folderId, Title = "Anime" }],
            ScheduleResponse = new AnimeCalendarScheduleResponse
            {
                Entries =
                [
                    new AnimeCalendarEntryDto
                    {
                        BookmarkId = Guid.NewGuid(),
                        Title = "One Piece",
                        EpisodeNumber = 1170,
                        AiringAtUtc = DateTimeOffset.Now
                    }
                ],
                AiringCount = 1
            }
        };
        context.Services.AddSingleton<IBookmarkService>(fakeService);

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AnimeCalendar>(1);
            builder.CloseComponent();
        });

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".rec-folder-chip")));
        page.FindAll(".rec-folder-chip").First(b => b.TextContent.Trim() == "Anime").Click();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".acal-view-btn")));
        page.FindAll(".acal-view-btn").First(b => b.TextContent.Trim() == "Month").Click();

        // A day with an episode renders a cover-image badge in its cell (design 2a).
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".acal-month-cover")));
    }

    private sealed class FakeAnimeBookmarkService : IBookmarkService
    {
        public List<FolderTreeNodeDto> FolderTree { get; set; } = [];
        public AnimeCalendarScheduleResponse ScheduleResponse { get; set; } = new();

        public Task<List<FolderTreeNodeDto>> GetFolderTreeAsync(CancellationToken cancellationToken = default) => Task.FromResult(FolderTree);
        public Task<AnimeCalendarScheduleResponse> GetAnimeScheduleAsync(List<Guid> folderIds, CancellationToken cancellationToken = default)
            => Task.FromResult(folderIds.Count == 0 ? new AnimeCalendarScheduleResponse() : ScheduleResponse);

        public Task<List<BookmarkNodeDto>> GetBookmarksAsync(Guid parentId, CancellationToken cancellationToken = default) => Task.FromResult(new List<BookmarkNodeDto>());
        public Task<PagedResult<BookmarkNodeDto>> SearchBookmarksAsync(SearchRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new PagedResult<BookmarkNodeDto>());
        public Task<BookmarkNodeDto?> GetBookmarkAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<BookmarkNodeDto?>(null);
        public Task<BookmarkNodeDto> CreateBookmarkAsync(Guid parentId, string title, string? url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BookmarkNodeDto> CreateFolderAsync(Guid parentId, string title, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BookmarkNodeDto?> UpdateBookmarkAsync(Guid id, string title, string? url, int? version = null, CancellationToken cancellationToken = default) => Task.FromResult<BookmarkNodeDto?>(null);
        public Task<BookmarkNodeDto?> UpdateMetadataAsync(Guid id, BookmarkMetadataDto metadata, CancellationToken cancellationToken = default) => Task.FromResult<BookmarkNodeDto?>(null);
        public Task<BookmarkNodeDto?> MoveBookmarkAsync(Guid id, Guid newParentId, CancellationToken cancellationToken = default) => Task.FromResult<BookmarkNodeDto?>(null);
        public Task<BookmarkNodeDto?> MoveFolderAsync(Guid id, Guid newParentId, CancellationToken cancellationToken = default) => Task.FromResult<BookmarkNodeDto?>(null);
        public Task<bool> DeleteBookmarkAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<List<BookmarkNodeDto>> GetDeletedBookmarksAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<BookmarkNodeDto>());
        public Task<bool> RestoreBookmarkAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> ReorderBookmarksAsync(Guid parentId, List<ReorderRequest> items, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> BatchDeleteBookmarksAsync(List<Guid> ids, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<List<BookmarkNodeDto>> GetFavoritesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<BookmarkNodeDto>());
        public Task<List<string>> SuggestTagsAsync(string title, string? url, CancellationToken cancellationToken = default) => Task.FromResult(new List<string>());
        public Task<List<BookmarkNodeDto>> GetRecommendationsAsync(List<Guid> folderIds, int count = 30, CancellationToken cancellationToken = default) => Task.FromResult(new List<BookmarkNodeDto>());
        public Task<BookmarkNodeDto?> ArchiveBookmarkAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<BookmarkNodeDto?>(null);
        public Task<bool> TriggerLinkCheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsLinkCheckRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<TriageJobStatusDto> TriageDomainAsync(TriageDomainRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new TriageJobStatusDto());
        public Task<TriageJobStatusDto> GetTriageStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new TriageJobStatusDto());
        public Task<bool> TriggerAutoTaggerAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<AutoTaggerStatusDto> GetAutoTaggerStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AutoTaggerStatusDto());
        public Task<List<string>> SuggestAiTagsAsync(Guid bookmarkId, CancellationToken cancellationToken = default) => Task.FromResult(new List<string>());
        public Task<RetagAllResult> RetagAllAsync(bool overwrite, CancellationToken cancellationToken = default) => Task.FromResult(new RetagAllResult());
        public Task<List<TagCountDto>> GetTagsAsync(Guid? folderId = null, CancellationToken cancellationToken = default) => Task.FromResult(new List<TagCountDto>());
        public Task<BatchTagResponse> TagBatchAsync(BatchTagRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new BatchTagResponse());
        public Task<AiAutoTagSummaryDto> AiAutoTagFolderAsync(Guid folderId, bool forceRefresh = false, CancellationToken cancellationToken = default) => Task.FromResult(new AiAutoTagSummaryDto());
        public Task<AiAutoTagSummaryDto> AiAutoTagFolderBatchAsync(Guid folderId, AiAutoTagBatchRequestDto request, CancellationToken cancellationToken = default) => Task.FromResult(new AiAutoTagSummaryDto());
        public Task<AiTaggingSettingsDto> GetAiTaggingSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AiTaggingSettingsDto());
        public Task<AiTaggingSettingsDto> SaveAiTaggingSettingsAsync(AiTaggingSettingsDto settings, CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task<TestAiKeyResponse> TestAiTaggingKeyAsync(TestAiKeyRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new TestAiKeyResponse { Success = true, Message = "fake" });
        public Task<Dictionary<Guid, int>> GetUntaggedCountsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Dictionary<Guid, int>());
        public Task<bool> BulkSaveTagsAsync(BulkSaveTagsRequest request, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<List<AnimeMatchCandidateDto>> GetAnimeMatchCandidatesAsync(Guid bookmarkId, CancellationToken cancellationToken = default) => Task.FromResult(new List<AnimeMatchCandidateDto>());
        public Task<BookmarkNodeDto?> ConfirmAnimeMatchAsync(Guid bookmarkId, AnimeMatchCandidateDto candidate, CancellationToken cancellationToken = default) => Task.FromResult<BookmarkNodeDto?>(null);
        public Task<BookmarkNodeDto?> ClearAnimeMatchAsync(Guid bookmarkId, CancellationToken cancellationToken = default) => Task.FromResult<BookmarkNodeDto?>(null);
        public Task<AutoMatchAnimeResponse> AutoMatchAnimeAsync(List<Guid> folderIds, CancellationToken cancellationToken = default) => Task.FromResult(new AutoMatchAnimeResponse());
    }
}
