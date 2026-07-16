using BookmarkManager.Client.Components;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class FolderTreeTests
{
    [Fact]
    public async Task ClickFolder_TriggersFolderSelected()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();

        var folderId = Guid.NewGuid();
        var folders = new List<FolderTreeNodeDto>
        {
            new FolderTreeNodeDto
            {
                Id = folderId,
                Title = "Manga",
                Children = []
            }
        };

        Guid? selectedId = null;

        var component = context.Render<FolderTree>(parameters => parameters
            .Add(p => p.Folders, folders)
            .Add(p => p.FolderSelected, EventCallback.Factory.Create<Guid>(this, id => selectedId = id))
        );

        // Find the folder div element by class and click it
        var folderElement = component.Find(".folder-drop-target");
        Assert.NotNull(folderElement);

        folderElement.Click();

        // Verify that selection callback was called with correct folderId
        Assert.Equal(folderId, selectedId);
    }

    [Fact]
    public async Task FolderWithBookmarks_RendersCountBadge()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();

        var folders = new List<FolderTreeNodeDto>
        {
            new FolderTreeNodeDto { Id = Guid.NewGuid(), Title = "Manga", BookmarkCount = 3, Children = [] }
        };

        var component = context.Render<FolderTree>(parameters => parameters
            .Add(p => p.Folders, folders));

        var badge = component.Find(".folder-count-badge");
        Assert.Equal("3", badge.TextContent.Trim());
    }

    [Fact]
    public async Task FolderWithNoBookmarks_DoesNotRenderCountBadge()
    {
        await using var context = new BunitContext();
        context.Services.AddMudServices();

        var folders = new List<FolderTreeNodeDto>
        {
            new FolderTreeNodeDto { Id = Guid.NewGuid(), Title = "Empty Folder", BookmarkCount = 0, Children = [] }
        };

        var component = context.Render<FolderTree>(parameters => parameters
            .Add(p => p.Folders, folders));

        Assert.Empty(component.FindAll(".folder-count-badge"));
    }
}
