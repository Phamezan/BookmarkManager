using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Backup;

namespace BookmarkManager.UnitTests.Backup;

public sealed class BackupRetentionPlannerTests
{
    [Fact]
    public void SelectForDeletion_NeverRemovesNewestSuccess()
    {
        var now = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var manifests = Enumerable.Range(0, 35)
            .Select(i => new BackupManifest
            {
                Id = Guid.NewGuid(),
                Status = BackupManifestStatus.Succeeded,
                CreatedAt = now.AddDays(-i),
                FilePath = $"backup-{i}.db"
            })
            .ToList();

        var toDelete = BackupRetentionPlanner.SelectForDeletion(manifests, 30, 60, now);

        Assert.DoesNotContain(manifests.OrderByDescending(m => m.CreatedAt).First(), toDelete);
        Assert.Equal(5, toDelete.Count);
    }

    [Fact]
    public void SelectForDeletion_RemovesFailuresOlderThanRetentionAge()
    {
        var now = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var manifests = new List<BackupManifest>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Status = BackupManifestStatus.Failed,
                CreatedAt = now.AddDays(-90),
                Error = "backup_failed"
            }
        };

        var toDelete = BackupRetentionPlanner.SelectForDeletion(manifests, 30, 60, now);
        Assert.Single(toDelete);
    }
}
