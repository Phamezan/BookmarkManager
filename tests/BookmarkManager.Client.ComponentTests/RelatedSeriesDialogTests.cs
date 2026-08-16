using BookmarkManager.Client.Components;
using BookmarkManager.Client.ComponentTests.TestDoubles;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using Xunit;

namespace BookmarkManager.Client.ComponentTests;

public sealed class RelatedSeriesDialogTests
{
    [Fact]
    public async Task RelatedSeriesDialog_RendersCandidatesAndAllowsApply()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var sourceId = Guid.NewGuid();
        var candId = Guid.NewGuid();

        var fakeService = new FakeBookmarkService();
        BulkUpdateBookmarkStatusRequest? capturedBulkRequest = null;
        fakeService.OnBulkUpdateBookmarkStatus = req =>
        {
            capturedBulkRequest = req;
            return Task.FromResult(new BulkUpdateBookmarkStatusResponse
            {
                Results = [new BookmarkStatusItemResult { BookmarkId = candId, Outcome = BookmarkStatusOutcome.Changed }]
            });
        };

        context.Services.AddSingleton<IBookmarkService>(fakeService);

        var dialogService = context.Services.GetRequiredService<IDialogService>();

        var candidates = new List<RelatedSeriesCandidateDto>
        {
            new()
            {
                Id = candId,
                Title = "Reincarnated as a Slime - Season 2",
                CurrentStatus = "Ongoing",
                MatchScore = 0.95,
                MatchReason = "High title similarity (95%)",
                Selected = true
            }
        };

        var comp = context.Render(builder =>
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
        });

        var parameters = new DialogParameters<RelatedSeriesDialog>
        {
            { x => x.SourceBookmarkId, sourceId },
            { x => x.SourceTitle, "Reincarnated as a Slime - Season 1" },
            { x => x.TargetStatus, BookmarkReadingStatus.Completed },
            { x => x.InitialCandidates, candidates }
        };

        await comp.InvokeAsync(() => dialogService.ShowAsync<RelatedSeriesDialog>("Review Related Series", parameters));

        Assert.Contains("Reincarnated as a Slime - Season 2", comp.Markup);
        Assert.Contains("Completed", comp.Markup);
    }

    [Fact]
    public async Task RelatedSeriesDialog_EmptyCandidates_ShowsEmptyState()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var fakeService = new FakeBookmarkService();
        context.Services.AddSingleton<IBookmarkService>(fakeService);

        var dialogService = context.Services.GetRequiredService<IDialogService>();

        var comp = context.Render(builder =>
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
        });

        var parameters = new DialogParameters<RelatedSeriesDialog>
        {
            { x => x.SourceBookmarkId, Guid.NewGuid() },
            { x => x.SourceTitle, "Standalone Anime" },
            { x => x.TargetStatus, BookmarkReadingStatus.Completed },
            { x => x.InitialCandidates, [] }
        };

        await comp.InvokeAsync(() => dialogService.ShowAsync<RelatedSeriesDialog>("Review Related Series", parameters));

        Assert.Contains("No other related bookmarks found", comp.Markup);
    }

    [Fact]
    public async Task RelatedSeriesDialog_PartialFailure_ShowsAlertAndKeepsFailedItemsSelected()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var cand1 = Guid.NewGuid();
        var cand2 = Guid.NewGuid();

        var fakeService = new FakeBookmarkService();
        fakeService.OnBulkUpdateBookmarkStatus = _ =>
        {
            return Task.FromResult(new BulkUpdateBookmarkStatusResponse
            {
                Results =
                [
                    new BookmarkStatusItemResult { BookmarkId = cand1, Outcome = BookmarkStatusOutcome.Changed },
                    new BookmarkStatusItemResult { BookmarkId = cand2, Outcome = BookmarkStatusOutcome.Failed, Message = "Sync failed" }
                ]
            });
        };

        context.Services.AddSingleton<IBookmarkService>(fakeService);
        var dialogService = context.Services.GetRequiredService<IDialogService>();

        var candidates = new List<RelatedSeriesCandidateDto>
        {
            new()
            {
                Id = cand1,
                Title = "Slime Season 2",
                CurrentStatus = "Ongoing",
                Selected = true
            },
            new()
            {
                Id = cand2,
                Title = "Slime Season 3",
                CurrentStatus = "Ongoing",
                Selected = true
            }
        };

        var comp = context.Render(builder =>
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
        });

        var parameters = new DialogParameters<RelatedSeriesDialog>
        {
            { x => x.SourceBookmarkId, Guid.NewGuid() },
            { x => x.SourceTitle, "Slime Season 1" },
            { x => x.TargetStatus, BookmarkReadingStatus.Completed },
            { x => x.InitialCandidates, candidates }
        };

        await comp.InvokeAsync(() => dialogService.ShowAsync<RelatedSeriesDialog>("Review Related Series", parameters));

        // Click Apply button
        var applyButton = comp.Find("button.mud-button-filled-primary");
        await comp.InvokeAsync(() => applyButton.Click());

        // Verify partial failure alert is displayed
        Assert.Contains("1 failed", comp.Markup);
        Assert.Contains("retry the remaining items", comp.Markup);
        // Slime Season 3 should still be in candidates
        Assert.Contains("Slime Season 3", comp.Markup);
        // Slime Season 2 succeeded and was removed
        Assert.DoesNotContain("Slime Season 2", comp.Markup);
    }
}
