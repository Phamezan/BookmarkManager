using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

using BookmarkManager.Client.ComponentTests.TestDoubles;

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


}
