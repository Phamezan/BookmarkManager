using BookmarkManager.Client.Components;
using BookmarkManager.Client.ComponentTests.TestDoubles;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class AutoTaggerDialogTests
{
    [Fact]
    public async Task ConfirmAndSave_SendsOnlyChangedTitlesAndManuallyEditedTagIds()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var childFolderId = Guid.NewGuid();
        var bookmarkA = new BookmarkNodeDto
        {
            Id = Guid.NewGuid(),
            Title = "Bookmark A",
            Url = "https://example.com/a",
            Type = NodeType.Bookmark,
            ParentId = childFolderId
        };
        var bookmarkB = new BookmarkNodeDto
        {
            Id = Guid.NewGuid(),
            Title = "Bookmark B",
            Url = "https://example.com/b",
            Type = NodeType.Bookmark,
            ParentId = childFolderId
        };

        var fake = new FakeBookmarkService
        {
            FolderTree =
            [
                new FolderTreeNodeDto
                {
                    Id = Guid.NewGuid(),
                    Title = "Bookmarks Bar",
                    Children =
                    [
                        new FolderTreeNodeDto { Id = childFolderId, Title = "Manga", Children = [] }
                    ]
                }
            ],
            UntaggedCounts = new Dictionary<Guid, int> { [childFolderId] = 2 },
            Bookmarks = [bookmarkA, bookmarkB]
        };

        BulkSaveTagsRequest? capturedRequest = null;
        fake.OnBulkSaveTags = req =>
        {
            capturedRequest = req;
            return Task.FromResult(true);
        };

        context.Services.AddSingleton<IBookmarkService>(fake);

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2);
            builder.CloseComponent();
        });

        var dialogService = context.Services.GetRequiredService<IDialogService>();
        var dialogReference = await dialogService.ShowAsync<AutoTaggerDialog>("Auto Tagger");

        page.WaitForAssertion(() => Assert.Contains("Manga", page.Markup));

        // Select the only selectable folder's checkbox, then start tagging.
        var folderCheckbox = page.Find(".mud-checkbox-input");
        folderCheckbox.Change(true);

        page.WaitForAssertion(() =>
        {
            var startButton = page.FindAll("button").First(b => b.TextContent.Trim() == "Start Tagging");
            Assert.False(startButton.HasAttribute("disabled"));
        });

        page.FindAll("button").First(b => b.TextContent.Trim() == "Start Tagging").Click();

        page.WaitForAssertion(() =>
            Assert.Contains(page.FindAll("button"), b => b.TextContent.Trim() == "Next"), TimeSpan.FromSeconds(5));

        page.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();

        page.WaitForAssertion(() => Assert.Equal(4, page.FindAll("input").Count(i => i.GetAttribute("type") != "checkbox")));

        var reviewInputs = page.FindAll("input").Where(i => i.GetAttribute("type") != "checkbox").ToList();

        // Row 0 (bookmarkA): edit the title only.
        reviewInputs[0].Input("Bookmark A (Renamed)");
        reviewInputs[0].KeyDown(new KeyboardEventArgs { Key = "Enter" });

        // Row 1 (bookmarkB): edit tags only, leave title untouched.
        reviewInputs[3].Change("Isekai");
        reviewInputs[3].KeyDown(new KeyboardEventArgs { Key = "Enter" });

        page.FindAll("button").First(b => b.TextContent.Trim() == "Apply & Save").Click();

        page.WaitForAssertion(() => Assert.NotNull(capturedRequest));

        Assert.NotNull(capturedRequest!.Titles);
        Assert.Single(capturedRequest.Titles!);
        Assert.Equal("Bookmark A (Renamed)", capturedRequest.Titles![bookmarkA.Id]);
        Assert.False(capturedRequest.Titles!.ContainsKey(bookmarkB.Id));

        Assert.NotNull(capturedRequest.ManuallyEditedTagIds);
        Assert.Single(capturedRequest.ManuallyEditedTagIds!);
        Assert.Contains(bookmarkB.Id, capturedRequest.ManuallyEditedTagIds!);
        Assert.DoesNotContain(bookmarkA.Id, capturedRequest.ManuallyEditedTagIds!);
    }

    [Fact]
    public async Task AcceptAllSuggestions_AppliesSuggestedTitlesIntoSavePayload()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var childFolderId = Guid.NewGuid();
        var bookmarkA = new BookmarkNodeDto
        {
            Id = Guid.NewGuid(),
            Title = "Solo Leveling Ch. 1",
            Url = "https://example.com/a",
            Type = NodeType.Bookmark,
            ParentId = childFolderId
        };
        var bookmarkB = new BookmarkNodeDto
        {
            Id = Guid.NewGuid(),
            Title = "Already Canonical",
            Url = "https://example.com/b",
            Type = NodeType.Bookmark,
            ParentId = childFolderId
        };

        var fake = new FakeBookmarkService
        {
            FolderTree =
            [
                new FolderTreeNodeDto
                {
                    Id = Guid.NewGuid(),
                    Title = "Bookmarks Bar",
                    Children =
                    [
                        new FolderTreeNodeDto { Id = childFolderId, Title = "Manga", Children = [] }
                    ]
                }
            ],
            UntaggedCounts = new Dictionary<Guid, int> { [childFolderId] = 2 },
            Bookmarks = [bookmarkA, bookmarkB],
            TagBatchResponse = new BatchTagResponse
            {
                Tags = new Dictionary<Guid, List<string>>
                {
                    [bookmarkA.Id] = ["Manhwa"],
                    [bookmarkB.Id] = ["Manhwa"]
                },
                SuggestedTitles = new Dictionary<Guid, string?>
                {
                    [bookmarkA.Id] = "Solo Leveling — Chapter 1",
                    [bookmarkB.Id] = "Already Canonical"
                }
            }
        };

        BulkSaveTagsRequest? capturedRequest = null;
        fake.OnBulkSaveTags = req =>
        {
            capturedRequest = req;
            return Task.FromResult(true);
        };

        context.Services.AddSingleton<IBookmarkService>(fake);

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2);
            builder.CloseComponent();
        });

        var dialogService = context.Services.GetRequiredService<IDialogService>();
        await dialogService.ShowAsync<AutoTaggerDialog>("Auto Tagger");

        page.WaitForAssertion(() => Assert.Contains("Manga", page.Markup));

        page.Find(".mud-checkbox-input").Change(true);
        page.WaitForAssertion(() =>
        {
            var startButton = page.FindAll("button").First(b => b.TextContent.Trim() == "Start Tagging");
            Assert.False(startButton.HasAttribute("disabled"));
        });

        page.FindAll("button").First(b => b.TextContent.Trim() == "Start Tagging").Click();
        page.WaitForAssertion(() =>
            Assert.Contains(page.FindAll("button"), b => b.TextContent.Trim() == "Next"), TimeSpan.FromSeconds(5));
        page.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();

        page.WaitForAssertion(() =>
            Assert.Contains(page.FindAll("button"), b => b.TextContent.Trim() == "Accept all suggestions"));

        page.FindAll("button").First(b => b.TextContent.Trim() == "Accept all suggestions").Click();
        page.FindAll("button").First(b => b.TextContent.Trim() == "Apply & Save").Click();

        page.WaitForAssertion(() => Assert.NotNull(capturedRequest));

        Assert.NotNull(capturedRequest!.Titles);
        Assert.Single(capturedRequest.Titles!);
        Assert.Equal("Solo Leveling — Chapter 1", capturedRequest.Titles![bookmarkA.Id]);
        Assert.False(capturedRequest.Titles!.ContainsKey(bookmarkB.Id));
    }
}
