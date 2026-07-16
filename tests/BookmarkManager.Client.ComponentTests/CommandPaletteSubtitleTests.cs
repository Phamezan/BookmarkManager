using BookmarkManager.Client.Components.CommandPalette;
using BookmarkManager.Client.ComponentTests.TestDoubles;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class CommandPaletteSubtitleTests
{
    [Theory]
    [InlineData("A / B", "https://example.com/ch/1", "A / B · example.com")]
    [InlineData(null, "https://example.com/foo", "example.com")]
    [InlineData("", "https://example.com/foo", "example.com")]
    [InlineData("A / B", null, "A / B")]
    [InlineData("A / B", "not-a-url", "A / B")]
    [InlineData(null, null, "Bookmark")]
    [InlineData(null, "", "Bookmark")]
    public void FormatBookmarkSubtitle_BuildsLocationFirstSubtitle(
        string? folderPath, string? url, string expected)
    {
        var actual = CommandPalette.FormatBookmarkSubtitle(folderPath, url);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FormatBookmarkSubtitle_ValidUrl_ShowsHostOnly_NotPath()
    {
        var actual = CommandPalette.FormatBookmarkSubtitle(
            "Manga", "https://example.com/series/ch/124");
        Assert.Equal("Manga · example.com", actual);
        Assert.DoesNotContain("/series", actual);
    }

    [Fact]
    public async Task OpenPalette_NestedFolderBookmark_RendersBreadcrumbSubtitle()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var folderA = Guid.NewGuid();
        var folderB = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();

        var fakeBookmarks = new FakeBookmarkService
        {
            FolderTree =
            [
                new FolderTreeNodeDto
                {
                    Id = folderA,
                    Title = "A",
                    Children =
                    [
                        new FolderTreeNodeDto { Id = folderB, Title = "B" }
                    ]
                }
            ],
            Bookmarks =
            [
                new BookmarkNodeDto
                {
                    Id = bookmarkId,
                    ParentId = folderB,
                    Title = "Series Ch 1",
                    Url = "https://example.com/ch/1",
                    Type = NodeType.Bookmark,
                    Metadata = new BookmarkMetadataDto
                    {
                        Tags = ["action", "fantasy"]
                    }
                }
            ]
        };

        var paletteService = new CommandPaletteService();
        context.Services.AddSingleton<IBookmarkService>(fakeBookmarks);
        context.Services.AddSingleton<ICommandPaletteService>(paletteService);
        context.Services.AddSingleton(new KeyboardShortcutService());
        context.Services.AddSingleton(new PaletteFrecencyService(context.JSInterop.JSRuntime));
        context.Services.AddSingleton(new PaletteSearchHistoryService(context.JSInterop.JSRuntime));

        var cut = context.Render<CommandPalette>();
        await cut.InvokeAsync(() => paletteService.Open());
        cut.WaitForAssertion(() =>
        {
            var subtitle = cut.Find(".palette-item-subtitle");
            Assert.Equal("A / B · example.com", subtitle.TextContent.Trim());
        }, TimeSpan.FromSeconds(3));

        var row = cut.Find(".palette-item");
        Assert.Equal("Tags: action, fantasy", row.GetAttribute("title"));
    }
}
