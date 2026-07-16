using BookmarkManager.Client.Pages;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

using BookmarkManager.Client.ComponentTests.TestDoubles;

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
        context.Services.AddTransient<SyncSocketListener>();
        context.Services.AddSingleton<IBookmarkService>(bookmarkService);
        context.Services.AddSingleton<IExtensionConnectionService>(new ConnectedExtensionService());
        context.Services.AddSingleton<UndoService>();
        context.Services.AddSingleton<KeyboardShortcutService>();

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


}
