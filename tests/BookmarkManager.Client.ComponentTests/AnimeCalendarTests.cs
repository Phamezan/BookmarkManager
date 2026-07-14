using BookmarkManager.Client.Pages;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

using BookmarkManager.Client.ComponentTests.TestDoubles;

namespace BookmarkManager.Client.ComponentTests;

public sealed class AnimeCalendarTests
{
    [Fact]
    public async Task NoAnimeFolders_ShowsGoToAutotaggingEmptyState()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddTransient<FolderSelectionPersistence>();
        context.Services.AddTransient<SyncSocketListener>();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService());

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AnimeCalendar>(1);
            builder.CloseComponent();
        });

        page.WaitForAssertion(() =>
        {
            Assert.Contains("No anime-tagged bookmarks found", page.Markup);
            Assert.Contains("Go to autotagging", page.Markup);
        });
    }

    [Fact]
    public async Task NoFoldersSelected_ShowsChooseFoldersEmptyState()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddTransient<FolderSelectionPersistence>();
        context.Services.AddTransient<SyncSocketListener>();
        context.Services.AddSingleton<IBookmarkService>(new FakeBookmarkService
        {
            FolderTree = [new FolderTreeNodeDto { Id = Guid.NewGuid(), Title = "Anime" }]
        });

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AnimeCalendar>(1);
            builder.CloseComponent();
        });

        page.WaitForAssertion(() =>
            Assert.Contains("Choose folders to build your calendar", page.Markup));
    }

    [Fact]
    public async Task FolderChips_OnlyShowFoldersContainingAnimeBookmarks()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var animeFolderId = Guid.NewGuid();
        var otherFolderId = Guid.NewGuid();
        var fakeService = new FakeBookmarkService
        {
            FolderTree =
            [
                new FolderTreeNodeDto { Id = animeFolderId, Title = "Anime" },
                new FolderTreeNodeDto { Id = otherFolderId, Title = "Recipes" }
            ],
            AnimeFolderIds = [animeFolderId]
        };
        context.Services.AddTransient<FolderSelectionPersistence>();
        context.Services.AddTransient<SyncSocketListener>();
        context.Services.AddSingleton<IBookmarkService>(fakeService);

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AnimeCalendar>(1);
            builder.CloseComponent();
        });

        page.WaitForAssertion(() =>
        {
            var chips = page.FindAll(".rec-folder-chip");
            Assert.Contains(chips, chip => chip.TextContent.Trim() == "Anime");
            Assert.DoesNotContain(chips, chip => chip.TextContent.Trim() == "Recipes");
        });
    }

    [Fact]
    public async Task SelectingFolder_ShowsMatchButton_WhenScheduleHasUnmatchedBookmarks()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var folderId = Guid.NewGuid();
        var unmatchedBookmark = new BookmarkNodeDto { Id = Guid.NewGuid(), Title = "Naruto" };
        var fakeService = new FakeBookmarkService
        {
            FolderTree = [new FolderTreeNodeDto { Id = folderId, Title = "Anime" }],
            ScheduleResponse = new AnimeCalendarScheduleResponse
            {
                Entries = [],
                UnmatchedBookmarks = [unmatchedBookmark]
            }
        };
        context.Services.AddTransient<FolderSelectionPersistence>();
        context.Services.AddTransient<SyncSocketListener>();
        context.Services.AddSingleton<IBookmarkService>(fakeService);

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AnimeCalendar>(1);
            builder.CloseComponent();
        });

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".rec-folder-chip")));
        var folderChip = page.FindAll(".rec-folder-chip").First(b => b.TextContent.Trim() == "Anime");
        folderChip.Click();

        // The cluttered per-title banner is gone; unmatched bookmarks now surface only as a compact
        // "Match N new" action that triggers auto-match.
        page.WaitForAssertion(() =>
            Assert.Contains("Match 1 new", page.Markup));
    }

    [Fact]
    public async Task SelectingFolder_WithEntries_WeekViewRendersEpisode()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var folderId = Guid.NewGuid();
        var fakeService = new FakeBookmarkService
        {
            FolderTree = [new FolderTreeNodeDto { Id = folderId, Title = "Anime" }],
            ScheduleResponse = new AnimeCalendarScheduleResponse
            {
                Entries =
                [
                    new AnimeCalendarEntryDto
                    {
                        BookmarkId = Guid.NewGuid(),
                        Title = "Mushoku Tensei",
                        EpisodeNumber = 5,
                        AiringAtUtc = DateTimeOffset.Now
                    }
                ],
                AiringCount = 1
            }
        };
        context.Services.AddTransient<FolderSelectionPersistence>();
        context.Services.AddTransient<SyncSocketListener>();
        context.Services.AddSingleton<IBookmarkService>(fakeService);

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AnimeCalendar>(1);
            builder.CloseComponent();
        });

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".rec-folder-chip")));
        page.FindAll(".rec-folder-chip").First(b => b.TextContent.Trim() == "Anime").Click();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".acal-view-btn")));
        page.FindAll(".acal-view-btn").First(b => b.TextContent.Trim() == "Week").Click();

        // Week groups the week's episodes into a roadmap timeline card.
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Mushoku Tensei", page.Markup);
            Assert.Contains("Ep 5", page.Markup);
        }, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SwitchingToMonthView_ShowsEpisodeCountBadge()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var folderId = Guid.NewGuid();
        var fakeService = new FakeBookmarkService
        {
            FolderTree = [new FolderTreeNodeDto { Id = folderId, Title = "Anime" }],
            ScheduleResponse = new AnimeCalendarScheduleResponse
            {
                Entries =
                [
                    new AnimeCalendarEntryDto
                    {
                        BookmarkId = Guid.NewGuid(),
                        Title = "One Piece",
                        EpisodeNumber = 1170,
                        AiringAtUtc = DateTimeOffset.Now
                    }
                ],
                AiringCount = 1
            }
        };
        context.Services.AddTransient<FolderSelectionPersistence>();
        context.Services.AddTransient<SyncSocketListener>();
        context.Services.AddSingleton<IBookmarkService>(fakeService);

        var page = context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<AnimeCalendar>(1);
            builder.CloseComponent();
        });

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".rec-folder-chip")));
        page.FindAll(".rec-folder-chip").First(b => b.TextContent.Trim() == "Anime").Click();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".acal-view-btn")));
        page.FindAll(".acal-view-btn").First(b => b.TextContent.Trim() == "Month").Click();

        // A day with an episode renders a cover-image badge in its cell (design 2a).
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".acal-month-cover-mini")));
    }


}
