using System.Net;
using System.Net.Http.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Backup;
using BookmarkManager.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class BackupEndpointTests : IntegrationTestBase
{
    [Fact]
    public async Task CreateBackup_WritesSnapshotAndManifest()
    {
        using var client = Factory.CreateClient();
        using var response = await client.PostAsync("/api/backups", content: null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var manifest = await response.Content.ReadFromJsonAsync<BackupManifestDto>();
        Assert.NotNull(manifest);
        Assert.Equal(BackupManifestStatus.Succeeded, manifest!.Status);
        Assert.True(manifest.SizeBytes > 0);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.BackupManifests.FindAsync(manifest.Id);
        Assert.NotNull(row);
        Assert.True(File.Exists(row!.FilePath));
    }

    [Fact]
    public async Task CreateBackup_ConcurrentRequestReturns409()
    {
        using var client = Factory.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => client.PostAsync("/api/backups", content: null)));

        try
        {
            Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task DownloadBackup_StreamsSnapshotBytes()
    {
        using var client = Factory.CreateClient();
        using var createResponse = await client.PostAsync("/api/backups", content: null);
        var manifest = await createResponse.Content.ReadFromJsonAsync<BackupManifestDto>();
        Assert.NotNull(manifest);

        using var downloadResponse = await client.GetAsync($"/api/backups/{manifest!.Id}/download");
        downloadResponse.EnsureSuccessStatusCode();
        var bytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);

        var tempPath = Path.Combine(Path.GetTempPath(), $"bm-backup-test-{Guid.NewGuid():N}.db");
        await File.WriteAllBytesAsync(tempPath, bytes);
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={tempPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM BookmarkNodes;";
                var count = Convert.ToInt32(await command.ExecuteScalarAsync());
                Assert.True(count >= 0);
            }

            SqliteConnection.ClearAllPools();
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public async Task DeleteBackup_RemovesFileAndRow()
    {
        using var client = Factory.CreateClient();
        using var createResponse = await client.PostAsync("/api/backups", content: null);
        var manifest = await createResponse.Content.ReadFromJsonAsync<BackupManifestDto>();
        Assert.NotNull(manifest);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.BackupManifests.FindAsync(manifest!.Id);
        Assert.NotNull(row);
        var filePath = row!.FilePath;
        Assert.True(File.Exists(filePath));

        using var deleteResponse = await client.DeleteAsync($"/api/backups/{manifest.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.False(File.Exists(filePath!));

        using var verifyScope = Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null(await verifyDb.BackupManifests.FindAsync(manifest.Id));
    }

    [Fact]
    public async Task RestoreBackup_WrongConfirm_Returns400()
    {
        using var client = Factory.CreateClient();
        using var createResponse = await client.PostAsync("/api/backups", content: null);
        var manifest = await createResponse.Content.ReadFromJsonAsync<BackupManifestDto>();
        Assert.NotNull(manifest);

        using var response = await client.PostAsJsonAsync(
            $"/api/backups/{manifest!.Id}/restore",
            new RestoreBackupRequest { Confirm = "yes please" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RestoreBackup_UnknownId_Returns404()
    {
        using var client = Factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            $"/api/backups/{Guid.NewGuid()}/restore",
            new RestoreBackupRequest { Confirm = "RESTORE" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RestoreBackup_HappyPath_StagesPendingFileAndPreRestoreBackup()
    {
        using var client = Factory.CreateClient();
        using var createResponse = await client.PostAsync("/api/backups", content: null);
        var manifest = await createResponse.Content.ReadFromJsonAsync<BackupManifestDto>();
        Assert.NotNull(manifest);

        using var response = await client.PostAsJsonAsync(
            $"/api/backups/{manifest!.Id}/restore",
            new RestoreBackupRequest { Confirm = "RESTORE" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<BackupRestoreResultDto>();
        Assert.NotNull(result);
        Assert.Equal(manifest.Id, result!.RestoredBackupId);
        Assert.True(result.RestartRequired);
        Assert.NotNull(result.PreRestoreBackupId);

        var pendingPath = BackupPendingRestore.GetPendingRestorePath(Factory.ConnectionString);
        Assert.True(File.Exists(pendingPath));

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var preRestoreManifest = await db.BackupManifests.FindAsync(result.PreRestoreBackupId!.Value);
        Assert.NotNull(preRestoreManifest);
        Assert.Equal(BackupManifestTrigger.PreRestore, preRestoreManifest!.Trigger);
        Assert.Equal(BackupManifestStatus.Succeeded, preRestoreManifest.Status);
    }

    [Fact]
    public async Task CreateBackup_InsufficientDisk_RecordsFailedManifestWithError()
    {
        Factory.MinFreeDiskBytesOverride = long.MaxValue;
        using var client = Factory.CreateClient();

        using var response = await client.PostAsync("/api/backups", content: null);

        // Preflight failure still returns a Failed manifest body via 500 from controller when Status=Failed
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var failed = db.BackupManifests.Single(m => m.Status == BackupManifestStatus.Failed);
        Assert.Equal("insufficient_disk_space", failed.Error);
        Assert.Equal(0, failed.SizeBytes);
    }

    [Fact]
    public async Task RecoverInterrupted_MarksStaleRunningRowsFailed()
    {
        using var client = Factory.CreateClient();
        // Force host build so services are available.
        _ = client;

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var partialPath = Path.Combine(Path.GetTempPath(), $"bm-partial-{Guid.NewGuid():N}.db");
        await File.WriteAllTextAsync(partialPath, "partial");

        var staleId = Guid.NewGuid();
        db.BackupManifests.Add(new BackupManifest
        {
            Id = staleId,
            Name = "stale-running.db",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            Status = BackupManifestStatus.Running,
            Trigger = BackupManifestTrigger.Manual,
            FilePath = partialPath
        });
        await db.SaveChangesAsync();

        var backupService = scope.ServiceProvider.GetRequiredService<IBackupService>();
        await backupService.RecoverInterruptedBackupsAsync();

        // RecoverInterrupted uses its own DbContext; clear the test scope tracker and re-query.
        db.ChangeTracker.Clear();
        var row = await db.BackupManifests.AsNoTracking().SingleAsync(m => m.Id == staleId);
        Assert.Equal(BackupManifestStatus.Failed, row.Status);
        Assert.Equal("interrupted", row.Error);
        Assert.False(File.Exists(partialPath));
    }
}
