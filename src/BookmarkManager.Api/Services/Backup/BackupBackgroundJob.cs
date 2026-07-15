using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Backup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Backup;

public sealed class BackupBackgroundJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<BackupOptions> _options;
    private readonly ILogger<BackupBackgroundJob> _logger;

    public BackupBackgroundJob(
        IServiceScopeFactory scopeFactory,
        IOptions<BackupOptions> options,
        ILogger<BackupBackgroundJob> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Backup background job started.");

        try
        {
            await TryCatchUpAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Backup startup catch-up failed; continuing schedule loop.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = ComputeDelayUntilNextRun(DateTime.UtcNow);
            _logger.LogInformation("Next scheduled backup in {Delay}.", delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (TaskCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await RunScheduledBackupAsync(stoppingToken);
        }
    }

    private async Task TryCatchUpAsync(CancellationToken ct)
    {
        var options = _options.Value;
        if (!options.Enabled)
        {
            _logger.LogInformation("Scheduled backups disabled; skipping startup catch-up.");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var backupService = scope.ServiceProvider.GetRequiredService<IBackupService>();
        var stats = await backupService.GetStatsAsync(ct);
        var lastSuccess = stats.LastBackup?.CreatedAt;
        if (lastSuccess.HasValue && lastSuccess.Value > DateTime.UtcNow.AddHours(-24))
        {
            return;
        }

        _logger.LogInformation("Last successful backup older than 24h; running catch-up backup.");
        await RunBackupSafelyAsync(backupService, BackupManifestTrigger.Scheduled, ct);
    }

    private async Task RunScheduledBackupAsync(CancellationToken ct)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("Scheduled backups disabled; skipping run.");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var backupService = scope.ServiceProvider.GetRequiredService<IBackupService>();
        await RunBackupSafelyAsync(backupService, BackupManifestTrigger.Scheduled, ct);
    }

    private async Task RunBackupSafelyAsync(IBackupService backupService, string trigger, CancellationToken ct)
    {
        try
        {
            var manifest = await backupService.CreateBackupAsync(trigger, ct);
            if (manifest.Status == BackupManifestStatus.Failed)
            {
                _logger.LogWarning("Scheduled backup failed with error {Error}.", manifest.Error);
            }
        }
        catch (BackupAlreadyRunningException)
        {
            _logger.LogInformation("Scheduled backup skipped because another backup is running.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled backup threw an unexpected error.");
        }
    }

    internal TimeSpan ComputeDelayUntilNextRun(DateTime utcNow)
    {
        var options = _options.Value;
        var nextRunUtc = BackupScheduleHelper.GetNextScheduledRunUtc(
            utcNow,
            options.ScheduleTime,
            options.TimeZoneId);
        var delay = nextRunUtc - utcNow;
        return delay <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : delay;
    }
}
