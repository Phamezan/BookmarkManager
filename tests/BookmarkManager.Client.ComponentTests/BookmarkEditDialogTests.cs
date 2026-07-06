using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarkEditDialogTests
{
    [Fact]
    public async Task EditingExistingBookmark_SaveIsEnabledWithoutTouchingOtherFields()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
        });

        var dialogService = context.Services.GetRequiredService<IDialogService>();
        var node = new BookmarkNodeDto
        {
            Id = Guid.NewGuid(),
            Title = "Existing bookmark",
            Url = "https://example.com",
            Metadata = new BookmarkMetadataDto { Tags = ["Manga", "Action"] }
        };

        var dialogReference = await dialogService.ShowAsync<BookmarkEditDialog>(
            "Edit Bookmark",
            new DialogParameters { ["Node"] = node });

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".mud-dialog")));

        // Save must be enabled immediately since Title/URL are already valid -
        // the user shouldn't have to touch another field first just to unlock Save.
        page.WaitForAssertion(() =>
        {
            var saveButton = page.FindAll("button").First(b => b.TextContent.Trim() == "Save");
            Assert.False(saveButton.HasAttribute("disabled"));
        });
    }

    [Fact]
    public async Task RemovingAllTags_StaysSavableAndPersistsEmptyTagList()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
        });

        var dialogService = context.Services.GetRequiredService<IDialogService>();
        var node = new BookmarkNodeDto
        {
            Id = Guid.NewGuid(),
            Title = "Existing bookmark",
            Url = "https://example.com",
            Metadata = new BookmarkMetadataDto { Tags = ["Manga", "Action"] }
        };

        var dialogReference = await dialogService.ShowAsync<BookmarkEditDialog>(
            "Edit Bookmark",
            new DialogParameters { ["Node"] = node });

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".mud-chip").Count));

        // Remove every tag chip one at a time, like a user clicking each close (x) icon.
        while (page.FindAll(".mud-chip").Count > 0)
        {
            page.FindAll(".mud-chip-close-button").First().Click();
        }

        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".mud-chip")));

        var saveButton = page.FindAll("button").First(b => b.TextContent.Trim() == "Save");
        Assert.False(saveButton.HasAttribute("disabled"));

        saveButton.Click();

        var result = await dialogReference!.Result;
        Assert.NotNull(result);
        Assert.False(result!.Canceled);
        var data = Assert.IsType<BookmarkEditDialog.BookmarkEditResult>(result.Data);
        Assert.Empty(data.Tags);
    }

    private sealed class FakeBookmarkService : IBookmarkService
    {
        public Task<List<FolderTreeNodeDto>> GetFolderTreeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<FolderTreeNodeDto>());
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
        public Task<AnimeCalendarScheduleResponse> GetAnimeScheduleAsync(List<Guid> folderIds, CancellationToken cancellationToken = default) => Task.FromResult(new AnimeCalendarScheduleResponse());
        public Task<AutoMatchAnimeResponse> AutoMatchAnimeAsync(List<Guid> folderIds, CancellationToken cancellationToken = default) => Task.FromResult(new AutoMatchAnimeResponse());
    }
}
