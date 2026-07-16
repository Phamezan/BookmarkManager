using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Backup;

namespace BookmarkManager.UnitTests.Backup;

public sealed class BackupFilenameFormatterTests
{
    [Fact]
    public void Create_IncludesTriggerSlugSecondsAndUniqueSuffix()
    {
        var timestamp = new DateTimeOffset(2026, 7, 15, 3, 15, 42, TimeSpan.FromHours(2));
        var name = BackupFilenameFormatter.Create(timestamp, BackupManifestTrigger.PreRestore);

        Assert.StartsWith("bookmarks-2026-07-15-031542-prerestore-", name);
        Assert.EndsWith(".db", name);
        Assert.Matches(@"^bookmarks-\d{4}-\d{2}-\d{2}-\d{6}-prerestore-[a-f0-9]{8}\.db$", name);
    }

    [Fact]
    public void Create_DifferentiatesTriggersInSameMinute()
    {
        var timestamp = new DateTimeOffset(2026, 7, 15, 3, 0, 0, TimeSpan.FromHours(2));
        var manual = BackupFilenameFormatter.Create(timestamp, BackupManifestTrigger.Manual);
        var scheduled = BackupFilenameFormatter.Create(timestamp, BackupManifestTrigger.Scheduled);

        Assert.Contains("-manual-", manual);
        Assert.Contains("-scheduled-", scheduled);
        Assert.NotEqual(manual, scheduled);
    }
}
