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
}
