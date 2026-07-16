using BookmarkManager.Api.Data;

namespace BookmarkManager.Api.Services.Backup;

public static class BackupRetentionPlanner
{
    public static IReadOnlyList<BackupManifest> SelectForDeletion(
        IReadOnlyList<BackupManifest> manifests,
        int retentionMaxCount,
        int retentionMaxAgeDays,
        DateTime utcNow)
    {
        var succeeded = manifests
            .Where(m => m.Status == BackupManifestStatus.Succeeded && !string.IsNullOrWhiteSpace(m.FilePath))
            .OrderByDescending(m => m.CreatedAt)
            .ToList();

        var newestSuccessId = succeeded.FirstOrDefault()?.Id;
        var cutoff = utcNow.AddDays(-retentionMaxAgeDays);
        var toDelete = new List<BackupManifest>();

        for (var i = 0; i < succeeded.Count; i++)
        {
            var manifest = succeeded[i];
            if (manifest.Id == newestSuccessId)
            {
                continue;
            }

            var exceedsCount = i >= retentionMaxCount;
            var exceedsAge = manifest.CreatedAt < cutoff;
            if (exceedsCount || exceedsAge)
            {
                toDelete.Add(manifest);
            }
        }

        var failedCutoff = utcNow.AddDays(-retentionMaxAgeDays);
        toDelete.AddRange(manifests
            .Where(m => m.Status == BackupManifestStatus.Failed && m.CreatedAt < failedCutoff));

        return toDelete
            .DistinctBy(m => m.Id)
            .ToList();
    }
}
