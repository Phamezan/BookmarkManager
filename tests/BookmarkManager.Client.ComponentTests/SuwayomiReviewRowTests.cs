using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Client.ComponentTests.TestDoubles;
using BookmarkManager.Client.Pages;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class SuwayomiReviewRowTests
{
    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage(BunitContext context)
    {
        return context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudBlazor.MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<UrlMigrator>(3);
            builder.CloseComponent();
        });
    }

    private static BunitContext CreateContext(FakeSuwayomiService fake)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBookmarkService>(fake);
        return context;
    }

    private static FakeSuwayomiService CreateFake(params UrlMigrationProposalDto[] proposals) => new()
    {
        SuwayomiStatus = new SuwayomiStatusDto { Reachable = true, Version = "2.4.2366", SourceCount = 19, SearchOrder = ["Asura Scans"] },
        SuwayomiImportStatus = new SuwayomiImportStatusDto { RunId = Guid.NewGuid(), FolderTitle = "Manga" },
        UrlMigrationProposals = [.. proposals]
    };

    private static UrlMigrationProposalDto MakeSuwayomiProposal(
        string confidence, string seriesName, string matchedTitle, string chapterNumber = "330", string? sourceLatest = "332")
    {
        const int mangaId = 2153;
        return new UrlMigrationProposalDto
        {
            Id = Guid.NewGuid(),
            BookmarkId = Guid.NewGuid(),
            BookmarkTitle = $"{seriesName} - Chapter {chapterNumber} - WEBTOON XYZ",
            OldUrl = "https://webtoon.xyz/read/nano-machine/chapter-330/",
            ProposedUrl = $"http://phamezan.capybara-pirarucu.ts.net:4567/manga/{mangaId}",
            ProposedHost = "phamezan.capybara-pirarucu.ts.net",
            SeriesName = seriesName,
            ChapterNumber = chapterNumber,
            Confidence = confidence,
            Detail = "Asura Scans · ch 330 of 332",
            Status = "Pending",
            CreatedAt = DateTime.UtcNow,
            SuwayomiMangaId = mangaId,
            SourceName = "Asura Scans",
            MatchedTitle = matchedTitle,
            SourceLatestChapter = sourceLatest
        };
    }

    [Fact]
    public async Task SuwayomiRow_RendersCoverAndSourceChip_AndNoNewUrlRow()
    {
        await using var context = CreateContext(CreateFake(MakeSuwayomiProposal("High", "Nano Machine", "Nano Machine")));
        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".migrator-suwa-row")));

        var row = page.Find(".migrator-suwa-row");
        Assert.NotEmpty(row.QuerySelectorAll(".migrator-suwa-cover"));
        Assert.Contains("Asura Scans", row.QuerySelector(".migrator-suwa-source")!.TextContent);
        Assert.Empty(row.QuerySelectorAll(".migrator-url-tag-new"));
        Assert.NotEmpty(row.QuerySelectorAll(".migrator-url-tag-old"));
    }

    [Fact]
    public async Task FromBookmark_HiddenForExactHighMatch()
    {
        await using var context = CreateContext(CreateFake(MakeSuwayomiProposal("High", "Nano Machine", "Nano Machine")));
        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".migrator-suwa-row")));

        Assert.Empty(page.FindAll(".migrator-suwa-from"));
    }

    [Fact]
    public async Task FromBookmark_ShownForMediumMatch()
    {
        await using var context = CreateContext(CreateFake(MakeSuwayomiProposal("Medium", "Nano Machine", "Nano Machines")));
        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".migrator-suwa-from")));
        Assert.Contains("From bookmark:", page.Find(".migrator-suwa-from").TextContent);
    }

    [Fact]
    public async Task ChapterBeyondSource_RendersWarningAndPickAnotherSourceAction()
    {
        await using var context = CreateContext(CreateFake(
            MakeSuwayomiProposal("High", "Nano Machine", "Nano Machine", chapterNumber: "190", sourceLatest: "156")));
        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".migrator-suwa-warn")));
        var warning = page.Find(".migrator-suwa-warn");
        Assert.Contains("ends at 156", warning.TextContent);
        Assert.NotEmpty(warning.QuerySelectorAll(".migrator-suwa-pick-source-btn"));
    }

    [Fact]
    public async Task ImportButton_DisabledWhenSuwayomiUnreachable()
    {
        var fake = CreateFake(MakeSuwayomiProposal("High", "Nano Machine", "Nano Machine"));
        fake.SuwayomiStatus = new SuwayomiStatusDto { Reachable = false, SearchOrder = [] };
        await using var context = CreateContext(fake);
        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".migrator-suwa-start-btn")));
        Assert.True(page.Find(".migrator-suwa-start-btn").HasAttribute("disabled"));
        Assert.Contains("Suwayomi unreachable", page.Markup);
    }

    private sealed class FakeSuwayomiService : FakeBookmarkService
    {
        public SuwayomiStatusDto? SuwayomiStatus { get; set; }
        public SuwayomiImportStatusDto? SuwayomiImportStatus { get; set; }

        public override Task<SuwayomiStatusDto?> GetSuwayomiStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(SuwayomiStatus);

        public override Task<SuwayomiImportStatusDto?> GetSuwayomiImportStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(SuwayomiImportStatus);
    }
}
