using BookmarkManager.Client.Pages;
using BookmarkManager.Client.Services;
using BookmarkManager.Client.ComponentTests.TestDoubles;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class MindMapPageTests
{
    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage(BunitContext context)
    {
        return context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MindMap>(1);
            builder.CloseComponent();
        });
    }

    private static BunitContext CreateContext(FakeBookmarkService fake)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(fake);
        return context;
    }

    private static List<MindMapNodeDto> SampleTree()
    {
        var rootId = Guid.NewGuid();
        var animeId = Guid.NewGuid();
        var devId = Guid.NewGuid();
        return
        [
            new MindMapNodeDto { Id = rootId, ParentId = null, Type = NodeType.Folder, Title = "", Position = 0 },
            new MindMapNodeDto { Id = animeId, ParentId = rootId, Type = NodeType.Folder, Title = "Anime", Position = 0 },
            new MindMapNodeDto { Id = devId, ParentId = rootId, Type = NodeType.Folder, Title = "Dev", Position = 1 },
            new MindMapNodeDto { Id = Guid.NewGuid(), ParentId = animeId, Type = NodeType.Bookmark, Title = "Frieren", Url = "https://example.com/frieren", Position = 0 },
            new MindMapNodeDto { Id = Guid.NewGuid(), ParentId = devId, Type = NodeType.Bookmark, Title = "GitHub", Url = "https://github.com", Position = 0 },
        ];
    }

    [Fact]
    public async Task RendersHostAndStats_WhenTreeLoads()
    {
        var fake = new FakeBookmarkService { MindMapNodes = SampleTree() };
        await using var context = CreateContext(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() =>
        {
            Assert.NotNull(page.Find("#mindmap-host"));
            Assert.Contains("3 folders", page.Markup);
            Assert.Contains("2 bookmarks", page.Markup);
        });
    }

    [Fact]
    public async Task LegendListsTopLevelFolders_SkippingUnnamedRoot()
    {
        var fake = new FakeBookmarkService { MindMapNodes = SampleTree() };
        await using var context = CreateContext(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() =>
        {
            var legendItems = page.FindAll(".mindmap-legend-item");
            Assert.Equal(2, legendItems.Count);
            Assert.Contains("Anime", legendItems[0].TextContent);
            Assert.Contains("Dev", legendItems[1].TextContent);
        });
    }

    [Fact]
    public async Task ShowsEmptyState_WhenNoNodes()
    {
        var fake = new FakeBookmarkService { MindMapNodes = [] };
        await using var context = CreateContext(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.Contains("Nothing to map yet", page.Markup));
    }

    [Fact]
    public async Task InitializesGraph_WithLoadedNodes()
    {
        var fake = new FakeBookmarkService { MindMapNodes = SampleTree() };
        await using var context = CreateContext(fake);
        context.JSInterop.Setup<string>("bookmarkMindMap.getStyle").SetResult("orbital");
        var init = context.JSInterop.SetupVoid("bookmarkMindMap.init", _ => true);

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.Single(init.Invocations));
    }
}
