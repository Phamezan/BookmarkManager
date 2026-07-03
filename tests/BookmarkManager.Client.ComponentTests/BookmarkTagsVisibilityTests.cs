using BookmarkManager.Client.Pages;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarkTagsVisibilityTests
{
    [Fact]
    public async Task RootFolderSelection_DoesNotRenderTagBar()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        var bookmarksBarId = Guid.NewGuid();
        var mangaId = Guid.NewGuid();
        var bookmarkService = new FakeBookmarkService
        {
            FolderTree =
            [
                new FolderTreeNodeDto
                {
                    Id = bookmarksBarId,
                    Title = "Bookmarks bar",
                    Children =
                    [
                        new FolderTreeNodeDto { Id = mangaId, Title = "Manga" }
                    ]
                }
            ],
            Tags = [new TagCountDto { Tag = "Action", Count = 12 }]
        };

        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(bookmarkService);
        context.Services.AddSingleton<IExtensionConnectionService>(new ConnectedExtensionService());
        context.Services.AddSingleton<UndoService>();

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<Bookmarks>(1);
            builder.CloseComponent();
        });
        page.WaitForAssertion(() => Assert.Equal(bookmarksBarId, bookmarkService.LastBookmarkFolderId));

        Assert.Empty(page.FindAll(".tag-bar"));
        Assert.Null(bookmarkService.LastTagsFolderId);
    }

    private sealed class ConnectedExtensionService : IExtensionConnectionService
    {
        public bool IsConnected => true;
        public event Action? ConnectionStateChanged { add { } remove { } }
        public Task PollAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeBookmarkService : IBookmarkService
    {
        public List<FolderTreeNodeDto> FolderTree { get; set; } = [];
        public List<TagCountDto> Tags { get; set; } = [];
        public Guid? LastBookmarkFolderId { get; private set; }
        public Guid? LastTagsFolderId { get; private set; }

        public Task<List<FolderTreeNodeDto>> GetFolderTreeAsync(CancellationToken cancellationToken = default) => Task.FromResult(FolderTree);
        public Task<List<BookmarkNodeDto>> GetBookmarksAsync(Guid parentId, CancellationToken cancellationToken = default)
        {
            LastBookmarkFolderId = parentId;
            return Task.FromResult(new List<BookmarkNodeDto>());
        }

        public Task<List<TagCountDto>> GetTagsAsync(Guid? folderId = null, CancellationToken cancellationToken = default)
        {
            LastTagsFolderId = folderId;
            return Task.FromResult(Tags);
        }

        public Task<List<BookmarkNodeDto>> GetFavoritesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<BookmarkNodeDto>());
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
        public Task<List<string>> SuggestTagsAsync(string title, string? url, CancellationToken cancellationToken = default) => Task.FromResult(new List<string>());
        public Task<List<BookmarkNodeDto>> GetStaleBookmarksAsync(int days, CancellationToken cancellationToken = default) => Task.FromResult(new List<BookmarkNodeDto>());
        public Task<BookmarkNodeDto?> ArchiveBookmarkAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<BookmarkNodeDto?>(null);
        public Task<bool> TriggerLinkCheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsLinkCheckRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> TriggerAutoTaggerAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<AutoTaggerStatusDto> GetAutoTaggerStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AutoTaggerStatusDto());
        public Task<List<string>> SuggestAiTagsAsync(Guid bookmarkId, CancellationToken cancellationToken = default) => Task.FromResult(new List<string>());
        public Task<RetagAllResult> RetagAllAsync(bool overwrite, CancellationToken cancellationToken = default) => Task.FromResult(new RetagAllResult());
        public Task<BatchTagResponse> TagBatchAsync(BatchTagRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new BatchTagResponse());
        public Task<AiAutoTagSummaryDto> AiAutoTagFolderAsync(Guid folderId, bool forceRefresh = false, CancellationToken cancellationToken = default) => Task.FromResult(new AiAutoTagSummaryDto());
        public Task<AiAutoTagSummaryDto> AiAutoTagFolderBatchAsync(Guid folderId, AiAutoTagBatchRequestDto request, CancellationToken cancellationToken = default) => Task.FromResult(new AiAutoTagSummaryDto());
        public Task<AiTaggingSettingsDto> GetAiTaggingSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AiTaggingSettingsDto());
        public Task<AiTaggingSettingsDto> SaveAiTaggingSettingsAsync(AiTaggingSettingsDto settings, CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task<Dictionary<Guid, int>> GetUntaggedCountsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Dictionary<Guid, int>());
        public Task<bool> BulkSaveTagsAsync(BulkSaveTagsRequest request, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
