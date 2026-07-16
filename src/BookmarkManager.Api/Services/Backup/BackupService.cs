using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Backup;

public sealed class BackupService : IBackupService
{
    private static readonly TimeSpan InterruptedBackupThreshold = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IOptions<BackupOptions> _options;
    private readonly IMapper _mapper;
    private readonly ILogger<BackupService> _logger;
    private readonly SemaphoreSlim _backupLock = new(1, 1);

    public BackupService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IOptions<BackupOptions> options,
        IMapper mapper,
        ILogger<BackupService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _options = options;
        _mapper = mapper;
        _logger = logger;
    }

    public async Task RecoverInterruptedBackupsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cutoff = DateTime.UtcNow.Subtract(InterruptedBackupThreshold);
        var stale = await db.BackupManifests
            .Where(m => m.Status == BackupManifestStatus.Running && m.CreatedAt < cutoff)
            .ToListAsync(ct);

        foreach (var manifest in stale)
        {
            manifest.Status = BackupManifestStatus.Failed;
            manifest.Error = "interrupted";
            DeleteFileIfExists(manifest.FilePath);
            _logger.LogWarning("Marked interrupted backup {BackupId} as failed.", manifest.Id);
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<BackupManifestDto> CreateBackupAsync(string trigger, CancellationToken ct = default)
    {
        if (!await _backupLock.WaitAsync(0, ct))
        {
            throw new BackupAlreadyRunningException();
        }

        try
        {
            return await CreateBackupCoreAsync(trigger, ct);
        }
        finally
        {
            _backupLock.Release();
        }
    }

    public async Task<BackupRestoreResultDto> ScheduleRestoreAsync(Guid id, string confirm, CancellationToken ct = default)
    {
        if (!string.Equals(confirm, "RESTORE", StringComparison.Ordinal))
        {
            throw new BackupInvalidConfirmException();
        }

        if (!await _backupLock.WaitAsync(0, ct))
        {
            throw new BackupAlreadyRunningException();
        }

        try
        {
            return await ScheduleRestoreCoreAsync(id, ct);
        }
        finally
        {
            _backupLock.Release();
        }
    }

    public async Task<IReadOnlyList<BackupManifestDto>> GetBackupsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manifests = await db.BackupManifests
            .AsNoTracking()
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);

        return _mapper.Map<List<BackupManifestDto>>(manifests);
    }

    public async Task<BackupStatsDto> GetStatsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var options = _options.Value;
        var utcNow = DateTime.UtcNow;
        var manifests = await db.BackupManifests.AsNoTracking().ToListAsync(ct);
        var backupDirectory = ResolveBackupDirectory(options);
        var succeeded = manifests
            .Where(m => m.Status == BackupManifestStatus.Succeeded)
            .OrderByDescending(m => m.CreatedAt)
            .ToList();

        var (rate, successCount, totalRuns) = BackupStatsAggregator.ComputeSuccessRate30d(manifests, utcNow);

        return new BackupStatsDto
        {
            LastBackup = succeeded.FirstOrDefault() is { } last
                ? _mapper.Map<BackupManifestDto>(last)
                : null,
            NextScheduledRunUtc = options.Enabled
                ? BackupScheduleHelper.GetNextScheduledRunUtc(utcNow, options.ScheduleTime, options.TimeZoneId)
                : null,
            FileCount = succeeded.Count(m => FileExistsUnderDirectory(m.FilePath, backupDirectory)),
            DiskUsedBytes = succeeded
                .Where(m => FileExistsUnderDirectory(m.FilePath, backupDirectory))
                .Sum(m => m.SizeBytes),
            LiveDatabaseSizeBytes = GetLiveDatabaseSizeBytes(),
            SuccessRate30d = rate,
            SuccessCount30d = successCount,
            TotalRuns30d = totalRuns,
            Activity = BackupStatsAggregator.BuildActivitySeries(manifests, utcNow, options.TimeZoneId),
            Enabled = options.Enabled,
            ScheduleTime = options.ScheduleTime,
            TimeZoneId = options.TimeZoneId,
            RetentionMaxCount = options.RetentionMaxCount,
            RetentionMaxAgeDays = options.RetentionMaxAgeDays,
            BackupDirectory = backupDirectory
        };
    }

    public async Task<(Stream Stream, string FileName)> OpenBackupAsync(Guid id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manifest = await db.BackupManifests.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new BackupNotFoundException();

        if (manifest.Status != BackupManifestStatus.Succeeded || string.IsNullOrWhiteSpace(manifest.FilePath))
        {
            throw new BackupNotFoundException();
        }

        var backupDirectory = ResolveBackupDirectory(_options.Value);
        var fullPath = Path.GetFullPath(manifest.FilePath);
        EnsurePathUnderDirectory(fullPath, backupDirectory);

        if (!File.Exists(fullPath))
        {
            throw new BackupNotFoundException();
        }

        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return (stream, manifest.Name);
    }

    public async Task DeleteBackupAsync(Guid id, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manifest = await db.BackupManifests.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new BackupNotFoundException();

        if (!string.IsNullOrWhiteSpace(manifest.FilePath))
        {
            var backupDirectory = ResolveBackupDirectory(_options.Value);
            var fullPath = Path.GetFullPath(manifest.FilePath);
            EnsurePathUnderDirectory(fullPath, backupDirectory);
            DeleteFileIfExists(fullPath);
        }

        db.BackupManifests.Remove(manifest);
        await db.SaveChangesAsync(ct);
    }

    private async Task<BackupManifestDto> CreateBackupCoreAsync(string trigger, CancellationToken ct)
    {
        var options = _options.Value;
        var backupDirectory = ResolveBackupDirectory(options);
        Directory.CreateDirectory(backupDirectory);

        if (!HasSufficientDiskSpace(backupDirectory, options.MinFreeDiskBytes))
        {
            return await RecordFailedBackupAsync(trigger, "insufficient_disk_space", ct);
        }

        var localTimestamp = BackupScheduleHelper.ToTimeZone(DateTime.UtcNow, options.TimeZoneId);
        var fileName = BackupFilenameFormatter.Create(localTimestamp, trigger);
        var targetPath = Path.Combine(backupDirectory, fileName);
        // GUID suffix makes collisions extremely unlikely; still regenerate if the path exists.
        var attempts = 0;
        while (File.Exists(targetPath) && attempts < 5)
        {
            fileName = BackupFilenameFormatter.Create(localTimestamp, trigger);
            targetPath = Path.Combine(backupDirectory, fileName);
            attempts++;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manifest = new BackupManifest
        {
            Id = Guid.NewGuid(),
            Name = fileName,
            CreatedAt = DateTime.UtcNow,
            Status = BackupManifestStatus.Running,
            Trigger = trigger,
            FilePath = targetPath
        };

        db.BackupManifests.Add(manifest);
        await db.SaveChangesAsync(ct);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await RunVacuumIntoAsync(targetPath, ct);
            var contentStats = await GatherContentStatsAsync(db, ct);
            var sizeBytes = new FileInfo(targetPath).Length;
            stopwatch.Stop();

            manifest.Status = BackupManifestStatus.Succeeded;
            manifest.SizeBytes = sizeBytes;
            manifest.DurationMs = stopwatch.ElapsedMilliseconds;
            manifest.BookmarkCount = contentStats.BookmarkCount;
            manifest.FolderCount = contentStats.FolderCount;
            manifest.TagCount = contentStats.TagCount;
            manifest.LibraryTitleCount = contentStats.LibraryTitleCount;
            manifest.Error = null;

            await db.SaveChangesAsync(ct);
            await ApplyRetentionAsync(db, ct);

            _logger.LogInformation(
                "Backup {BackupId} succeeded in {DurationMs}ms ({SizeBytes} bytes).",
                manifest.Id,
                manifest.DurationMs,
                manifest.SizeBytes);

            return _mapper.Map<BackupManifestDto>(manifest);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            manifest.Status = BackupManifestStatus.Failed;
            manifest.Error = MapBackupError(ex);
            manifest.DurationMs = stopwatch.ElapsedMilliseconds;
            manifest.SizeBytes = 0;
            DeleteFileIfExists(targetPath);
            await db.SaveChangesAsync(ct);

            _logger.LogError(ex, "Backup {BackupId} failed.", manifest.Id);
            return _mapper.Map<BackupManifestDto>(manifest);
        }
    }

    private async Task<BackupRestoreResultDto> ScheduleRestoreCoreAsync(Guid id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manifest = await db.BackupManifests.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new BackupNotFoundException();

        if (manifest.Status != BackupManifestStatus.Succeeded || string.IsNullOrWhiteSpace(manifest.FilePath))
        {
            throw new BackupNotFoundException();
        }

        var backupDirectory = ResolveBackupDirectory(_options.Value);
        var fullPath = Path.GetFullPath(manifest.FilePath);
        EnsurePathUnderDirectory(fullPath, backupDirectory);

        if (!File.Exists(fullPath))
        {
            throw new BackupNotFoundException();
        }

        await BackupPendingRestore.VerifySnapshotAsync(fullPath, ct);

        // Mandatory safety net: capture the current live state before staging the restore, in
        // case the chosen snapshot turns out to be wrong once applied.
        var preRestoreManifest = await CreateBackupCoreAsync(BackupManifestTrigger.PreRestore, ct);
        if (preRestoreManifest.Status == BackupManifestStatus.Failed)
        {
            throw new BackupRestoreException("Pre-restore safety backup failed; restore aborted.");
        }

        var connectionString = _configuration.GetConnectionString("Default") ?? "Data Source=bookmarks.db";
        BackupPendingRestore.StagePendingRestore(fullPath, connectionString);

        _logger.LogWarning(
            "Restore staged for backup {BackupId} (pre-restore safety backup {PreRestoreId}); restart required to apply.",
            id,
            preRestoreManifest.Id);

        return new BackupRestoreResultDto
        {
            RestoredBackupId = id,
            PreRestoreBackupId = preRestoreManifest.Id,
            RestartRequired = true,
            Message = "Restore staged. The application must restart to apply the snapshot."
        };
    }

    private async Task<BackupManifestDto> RecordFailedBackupAsync(string trigger, string error, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manifest = new BackupManifest
        {
            Id = Guid.NewGuid(),
            Name = BackupFilenameFormatter.Create(
                BackupScheduleHelper.ToTimeZone(DateTime.UtcNow, _options.Value.TimeZoneId),
                trigger),
            CreatedAt = DateTime.UtcNow,
            Status = BackupManifestStatus.Failed,
            Trigger = trigger,
            Error = error
        };

        db.BackupManifests.Add(manifest);
        await db.SaveChangesAsync(ct);
        _logger.LogWarning("Backup preflight failed with error {Error}.", error);
        return _mapper.Map<BackupManifestDto>(manifest);
    }

    private async Task RunVacuumIntoAsync(string targetPath, CancellationToken ct)
    {
        var connectionString = _configuration.GetConnectionString("Default") ?? "Data Source=bookmarks.db";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var escapedPath = targetPath.Replace("'", "''", StringComparison.Ordinal);
        command.CommandText = $"VACUUM INTO '{escapedPath}'";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(int BookmarkCount, int FolderCount, int TagCount, int LibraryTitleCount)> GatherContentStatsAsync(
        AppDbContext db,
        CancellationToken ct)
    {
        var bookmarkCount = await db.BookmarkNodes.CountAsync(
            n => n.Type == NodeType.Bookmark && !n.IsDeleted, ct);
        var folderCount = await db.BookmarkNodes.CountAsync(
            n => n.Type == NodeType.Folder && !n.IsDeleted, ct);
        var tagCount = await db.TagProvenances.Select(t => t.Tag).Distinct().CountAsync(ct);
        var libraryTitleCount = await db.LibraryCatalogEntries.CountAsync(ct);
        return (bookmarkCount, folderCount, tagCount, libraryTitleCount);
    }

    private async Task ApplyRetentionAsync(AppDbContext db, CancellationToken ct)
    {
        var options = _options.Value;
        var manifests = await db.BackupManifests.ToListAsync(ct);
        var toDelete = BackupRetentionPlanner.SelectForDeletion(
            manifests,
            options.RetentionMaxCount,
            options.RetentionMaxAgeDays,
            DateTime.UtcNow);

        foreach (var manifest in toDelete)
        {
            if (!string.IsNullOrWhiteSpace(manifest.FilePath))
            {
                var backupDirectory = ResolveBackupDirectory(options);
                var fullPath = Path.GetFullPath(manifest.FilePath);
                if (IsPathUnderDirectory(fullPath, backupDirectory))
                {
                    DeleteFileIfExists(fullPath);
                }
            }

            db.BackupManifests.Remove(manifest);
        }

        if (toDelete.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Retention pruning removed {Count} backup manifest(s).", toDelete.Count);
        }
    }

    private long GetLiveDatabaseSizeBytes()
    {
        var connectionString = _configuration.GetConnectionString("Default") ?? "Data Source=bookmarks.db";
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource))
        {
            return 0;
        }

        var dbPath = Path.GetFullPath(builder.DataSource);
        long size = 0;
        if (File.Exists(dbPath))
        {
            size += new FileInfo(dbPath).Length;
        }

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = dbPath + suffix;
            if (File.Exists(sidecar))
            {
                size += new FileInfo(sidecar).Length;
            }
        }

        return size;
    }

    private static bool HasSufficientDiskSpace(string directory, long minFreeDiskBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (string.IsNullOrWhiteSpace(root))
        {
            return true;
        }

        var drive = new DriveInfo(root);
        return drive.AvailableFreeSpace >= minFreeDiskBytes;
    }

    internal static string ResolveBackupDirectory(BackupOptions options)
    {
        if (Directory.Exists("/data"))
        {
            return options.Directory;
        }

        var fallback = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups", "db");
        return fallback;
    }

    private static bool FileExistsUnderDirectory(string? filePath, string backupDirectory)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(filePath);
        return IsPathUnderDirectory(fullPath, backupDirectory) && File.Exists(fullPath);
    }

    private static void EnsurePathUnderDirectory(string fullPath, string backupDirectory)
    {
        if (!IsPathUnderDirectory(fullPath, backupDirectory))
        {
            throw new BackupNotFoundException();
        }
    }

    private static bool IsPathUnderDirectory(string fullPath, string backupDirectory)
    {
        var normalizedDirectory = Path.GetFullPath(backupDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFile = Path.GetFullPath(fullPath);
        return normalizedFile.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || normalizedFile.Equals(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteFileIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup; retention and failure paths should not throw on delete races.
        }
    }

    private static string MapBackupError(Exception ex)
    {
        if (ex is SqliteException sqlite)
        {
            // SQLITE_FULL = 13 (primary error code). Prefer primary over extended.
            if ((int)sqlite.SqliteErrorCode == 13 || sqlite.SqliteExtendedErrorCode == 13)
            {
                return "database_full";
            }

            return "backup_failed";
        }

        return "backup_failed";
    }
}
