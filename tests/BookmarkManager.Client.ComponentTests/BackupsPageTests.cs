using BookmarkManager.Client.Pages;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BackupsPageTests
{
    private sealed class FakeBackupService : IBackupService
    {
        public bool CreateCalled { get; private set; }
        public string? LastRestoreConfirm { get; private set; }
        public Guid? LastRestoreId { get; private set; }

        public Task<IReadOnlyList<BackupManifestDto>> GetBackupsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<BackupManifestDto>>([
                new BackupManifestDto
                {
                    Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    Name = "bookmarks-2026-07-14-0300.db",
                    CreatedAt = DateTime.UtcNow.AddDays(-1),
                    Status = "Succeeded",
                    Trigger = "Scheduled",
                    SizeBytes = 2_000_000,
                    DurationMs = 900,
                    BookmarkCount = 12,
                    FolderCount = 3,
                    TagCount = 8,
                    LibraryTitleCount = 4
                }
            ]);

        public Task<BackupStatsDto> GetStatsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new BackupStatsDto
            {
                LastBackup = new BackupManifestDto
                {
                    Name = "bookmarks-2026-07-14-0300.db",
                    CreatedAt = DateTime.UtcNow.AddDays(-1),
                    Status = "Succeeded",
                    SizeBytes = 2_000_000,
                    BookmarkCount = 12,
                    FolderCount = 3,
                    TagCount = 8,
                    LibraryTitleCount = 4
                },
                FileCount = 1,
                DiskUsedBytes = 2_000_000,
                LiveDatabaseSizeBytes = 2_500_000,
                SuccessRate30d = 100,
                SuccessCount30d = 1,
                TotalRuns30d = 1,
                Activity =
                [
                    new BackupActivityDayDto
                    {
                        Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
                        Status = "Succeeded",
                        SizeBytes = 2_000_000,
                        DurationMs = 900,
                        BookmarkCount = 12
                    },
                    new BackupActivityDayDto
                    {
                        Date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)),
                        Status = "Failed",
                        Error = "database_full"
                    }
                ],
                Enabled = true,
                ScheduleTime = "03:00",
                TimeZoneId = "Europe/Berlin",
                RetentionMaxCount = 30,
                RetentionMaxAgeDays = 60,
                BackupDirectory = "/data/backups/db"
            });

        public async Task<BackupManifestDto> CreateBackupAsync(CancellationToken cancellationToken = default)
        {
            CreateCalled = true;
            await Task.Delay(50, cancellationToken);
            return new BackupManifestDto { Status = "Succeeded", Name = "bookmarks-new.db" };
        }

        public Task DeleteBackupAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<BackupRestoreResultDto> RestoreAsync(Guid id, string confirm, CancellationToken cancellationToken = default)
        {
            LastRestoreId = id;
            LastRestoreConfirm = confirm;
            return Task.FromResult(new BackupRestoreResultDto
            {
                RestoredBackupId = id,
                RestartRequired = true,
                Message = "Restore staged."
            });
        }

        public string GetDownloadUrl(Guid id) => $"api/backups/{id}/download";
    }

    [Fact]
    public async Task Page_RendersStatsAndHistory()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBackupService>(new FakeBackupService());

        var page = context.Render<Backups>();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Back up now", page.Markup);
            Assert.Contains("bookmarks-2026-07-14-0300.db", page.Markup);
            Assert.Contains("Live DB size", page.Markup);
        });
    }

    [Fact]
    public async Task BackUpNow_DisablesWhileRunning()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var fake = new FakeBackupService();
        context.Services.AddSingleton<IBackupService>(fake);

        var page = context.Render<Backups>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".backups-cta")));

        page.Find(".backups-cta").Click();
        page.WaitForAssertion(() => Assert.Contains("Backing up", page.Markup));
        await page.InvokeAsync(() => Task.Delay(100));
        page.WaitForAssertion(() => Assert.True(fake.CreateCalled));
    }

    [Fact]
    public async Task ActivityChart_RendersFailedDayDistinctly()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.Services.AddSingleton<IBackupService>(new FakeBackupService());

        var page = context.Render<Backups>();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("col fail", page.Markup);
            Assert.Contains("Failed", page.Markup);
            Assert.Contains("sw bad", page.Markup);
        });
    }

    [Fact]
    public async Task RestoreModal_RequiresExactRestoreText()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        var fake = new FakeBackupService();
        context.Services.AddSingleton<IBackupService>(fake);

        var page = context.Render<Backups>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.linkish")));

        var restoreButtons = page.FindAll("button.linkish")
            .Where(button => button.TextContent.Contains("Restore", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(restoreButtons);
        restoreButtons[0].Click();

        page.WaitForAssertion(() => Assert.NotNull(page.Find("#restore-confirm")));

        var restoreCta = page.FindAll(".backups-cta")
            .First(button => button.TextContent.Contains("Restore", StringComparison.Ordinal)
                             && button.TagName.Equals("BUTTON", StringComparison.OrdinalIgnoreCase));
        Assert.True(restoreCta.HasAttribute("disabled"));

        page.Find("#restore-confirm").Input("restore");
        Assert.True(restoreCta.HasAttribute("disabled"));
        Assert.Null(fake.LastRestoreConfirm);

        page.Find("#restore-confirm").Input("RESTORE");
        page.WaitForAssertion(() =>
        {
            restoreCta = page.FindAll(".backups-cta")
                .First(button => button.TextContent.Contains("Restore", StringComparison.Ordinal)
                                 && button.TagName.Equals("BUTTON", StringComparison.OrdinalIgnoreCase));
            Assert.False(restoreCta.HasAttribute("disabled"));
        });

        restoreCta.Click();
        page.WaitForAssertion(() =>
        {
            Assert.Equal("RESTORE", fake.LastRestoreConfirm);
            Assert.Contains("Restoring", page.Markup);
        });
    }
}
