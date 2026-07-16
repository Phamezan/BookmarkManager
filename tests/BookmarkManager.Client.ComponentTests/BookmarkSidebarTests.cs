using BookmarkManager.Client.Features.Bookmarks;
using BookmarkManager.Client.Features.Bookmarks.Components;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarkSidebarTests
{
    [Fact]
    public async Task ExpandAllButton_InvokesExpandAllFoldersCallback()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        var invoked = false;
        var folders = new List<FolderTreeNodeDto>
        {
            new FolderTreeNodeDto { Id = Guid.NewGuid(), Title = "Manga", Children = [] }
        };

        context.Render<MudPopoverProvider>();
        var component = context.Render<BookmarkSidebar>(parameters => parameters
            .Add(p => p.FolderTree, folders)
            .Add(p => p.ExpandAllFolders, EventCallback.Factory.Create(this, () => invoked = true)));

        component.Find("button[aria-label='Expand all folders']").Click();

        Assert.True(invoked);
    }

    [Fact]
    public async Task CollapseAllButton_InvokesCollapseAllFoldersCallback()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var invoked = false;
        var folders = new List<FolderTreeNodeDto>
        {
            new FolderTreeNodeDto { Id = Guid.NewGuid(), Title = "Manga", Children = [] }
        };

        context.Render<MudPopoverProvider>();
        var component = context.Render<BookmarkSidebar>(parameters => parameters
            .Add(p => p.FolderTree, folders)
            .Add(p => p.CollapseAllFolders, EventCallback.Factory.Create(this, () => invoked = true)));

        component.Find("button[aria-label='Collapse all folders']").Click();

        Assert.True(invoked);
    }

    // The two icon buttons above only wire an EventCallback — the actual expand-all /
    // collapse-all set computation lives in FolderExpansionHelper (used by Bookmarks.Tree.cs,
    // which owns folder-expansion state). Covered here directly since it's pure logic.

    private static List<FolderTreeNodeDto> BuildTree(out Guid rootId, out Guid childId, out Guid grandchildId, out Guid siblingId)
    {
        grandchildId = Guid.NewGuid();
        childId = Guid.NewGuid();
        siblingId = Guid.NewGuid();
        rootId = Guid.NewGuid();

        var tree = new List<FolderTreeNodeDto>
        {
            new FolderTreeNodeDto
            {
                Id = rootId,
                Title = "Root",
                Children =
                [
                    new FolderTreeNodeDto
                    {
                        Id = childId,
                        Title = "Child",
                        Children =
                        [
                            new FolderTreeNodeDto { Id = grandchildId, Title = "Grandchild", Children = [] }
                        ]
                    },
                    new FolderTreeNodeDto { Id = siblingId, Title = "Sibling", Children = [] }
                ]
            }
        };
        return tree;
    }

    [Fact]
    public void CollectAllFolderIds_ReturnsEveryFolderIdRecursively()
    {
        var tree = BuildTree(out var rootId, out var childId, out var grandchildId, out var siblingId);

        var ids = FolderExpansionHelper.CollectAllFolderIds(tree);

        Assert.Equal(4, ids.Count);
        Assert.Contains(rootId, ids);
        Assert.Contains(childId, ids);
        Assert.Contains(grandchildId, ids);
        Assert.Contains(siblingId, ids);
    }

    [Fact]
    public void CollapseAll_AlwaysReturnsEmptySet()
    {
        var keep = FolderExpansionHelper.CollapseAll();

        Assert.Empty(keep);
    }

    [Fact]
    public void CollapseAll_IgnoringSelectionOrExpansionState_StillReturnsEmptySet()
    {
        // Regression guard for true collapse-all: previously this kept ancestors of the
        // selected folder expanded. Now collapse-all always clears everything.
        BuildTree(out _, out var childId, out var grandchildId, out _);

        var keep = FolderExpansionHelper.CollapseAll();

        Assert.DoesNotContain(childId, keep);
        Assert.DoesNotContain(grandchildId, keep);
        Assert.Empty(keep);
    }
}
