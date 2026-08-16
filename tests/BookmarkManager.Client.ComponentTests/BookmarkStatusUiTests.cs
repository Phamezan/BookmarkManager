using BookmarkManager.Client.Features.Bookmarks.Components;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarkStatusUiTests
{
    [Theory]
    [InlineData(BookmarkReadingStatus.Ongoing, "Ongoing")]
    [InlineData(BookmarkReadingStatus.PlanToRead, "Plan to Read")]
    [InlineData(BookmarkReadingStatus.Completed, "Completed")]
    [InlineData(BookmarkReadingStatus.Dropped, "Dropped")]
    public async Task BookmarkCard_RendersPersonalStatusBadge(string status, string expectedLabel)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var item = new BookmarkNodeDto
        {
            Id = Guid.NewGuid(),
            Title = "Solo Leveling",
            Url = "https://example.com/sl",
            Type = NodeType.Bookmark,
            Metadata = new BookmarkMetadataDto
            {
                Status = status
            }
        };

        var comp = context.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<BookmarkCard>(1);
            builder.AddAttribute(2, "Item", item);
            builder.CloseComponent();
        });

        Assert.Contains(expectedLabel, comp.Markup);
    }

    [Fact]
    public async Task BookmarkCard_PendingSyncState_RendersPendingBadge()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var item = new BookmarkNodeDto
        {
            Id = Guid.NewGuid(),
            Title = "Solo Leveling",
            Url = "https://example.com/sl",
            Type = NodeType.Bookmark,
            SyncState = SyncState.Pending,
            Metadata = new BookmarkMetadataDto
            {
                Status = BookmarkReadingStatus.Completed
            }
        };

        var comp = context.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<BookmarkCard>(1);
            builder.AddAttribute(2, "Item", item);
            builder.CloseComponent();
        });

        Assert.Contains("Pending", comp.Markup);
    }

    [Fact]
    public async Task BookmarkCard_FailedSyncState_RendersFailedBadge()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var item = new BookmarkNodeDto
        {
            Id = Guid.NewGuid(),
            Title = "Solo Leveling",
            Url = "https://example.com/sl",
            Type = NodeType.Bookmark,
            SyncState = SyncState.Failed,
            Metadata = new BookmarkMetadataDto
            {
                Status = BookmarkReadingStatus.Completed
            }
        };

        var comp = context.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<BookmarkCard>(1);
            builder.AddAttribute(2, "Item", item);
            builder.CloseComponent();
        });

        Assert.Contains("Failed", comp.Markup);
        Assert.Contains("status-badge--error", comp.Markup);
    }

    [Fact]
    public async Task BookmarksToolbar_SelectedItems_RendersStatusActionMenu()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var comp = context.Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<BookmarksToolbar>(1);
            builder.AddAttribute(2, "SelectedCount", 2);
            builder.CloseComponent();
        });

        Assert.Contains("Status", comp.Markup);
    }
}
