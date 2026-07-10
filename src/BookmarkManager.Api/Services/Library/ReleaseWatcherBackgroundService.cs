using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Library;

public sealed class ReleaseWatcherBackgroundService : BackgroundService
{
    private const int MaximumBackoffHours = 24;
    private const double ScheduleJitterRatio = 0.1;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReleaseWatcherBackgroundService> _logger;
    private readonly LibraryProviderRegistry _registry;
    private readonly Channel<bool> _triggerChannel = Channel.CreateUnbounded<bool>();
    private readonly Channel<bool> _scheduleChangedChannel = Channel.CreateUnbounded<bool>();
    private readonly object _statusLock = new();
    private bool _isRunning;
    private bool _rerunRequested;
    private DateTimeOffset? _lastRunTime;
    private int _successCount;
    private int _failureCount;
    private string? _lastError;

    public ReleaseWatcherBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<ReleaseWatcherBackgroundService> _logger,
        LibraryProviderRegistry registry)
    {
        _scopeFactory = scopeFactory;
        this._logger = _logger;
        _registry = registry;
    }

    public void TriggerCheck()
    {
        lock (_statusLock)
        {
            if (_isRunning)
            {
                _rerunRequested = true;
                return;
            }
        }
        _triggerChannel.Writer.TryWrite(true);
    }

    public void NotifyScheduleChanged() => _scheduleChangedChannel.Writer.TryWrite(true);

    public ReleaseWatcherStatusDto GetStatus(int totalTrackedCount)
    {
        lock (_statusLock)
        {
            return new ReleaseWatcherStatusDto
            {
                IsRunning = _isRunning,
                LastRunTime = _lastRunTime,
                TotalTrackedCount = totalTrackedCount,
                SuccessCount = _successCount,
                FailureCount = _failureCount,
                LastError = _lastError
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Release watcher background service started.");

        // Run checking on interval
        _ = RunTimerLoopAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _triggerChannel.Reader.WaitToReadAsync(stoppingToken);
                while (_triggerChannel.Reader.TryRead(out _)) { }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            lock (_statusLock)
            {
                _isRunning = true;
                _rerunRequested = false;
            }

            try
            {
                await CheckReleasesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during release checking.");
                lock (_statusLock)
                {
                    _lastError = ex.Message;
                    _failureCount++;
                }
            }
            finally
            {
                bool shouldRerun;
                lock (_statusLock)
                {
                    _isRunning = false;
                    _lastRunTime = DateTimeOffset.UtcNow;
                    shouldRerun = _rerunRequested;
                    _rerunRequested = false;
                }
                if (shouldRerun)
                {
                    TriggerCheck();
                }
            }
        }
    }

    private async Task RunTimerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var interval = await GetConfiguredIntervalAsync(stoppingToken).ConfigureAwait(false);
                var jitterMultiplier = 1 + ((Random.Shared.NextDouble() * 2 - 1) * ScheduleJitterRatio);
                var delay = TimeSpan.FromTicks((long)(interval.Ticks * jitterMultiplier));
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delayTask = Task.Delay(delay, waitCts.Token);
                var scheduleChangedTask = _scheduleChangedChannel.Reader.ReadAsync(waitCts.Token).AsTask();
                var completedTask = await Task.WhenAny(delayTask, scheduleChangedTask).ConfigureAwait(false);
                waitCts.Cancel();

                if (completedTask == delayTask)
                {
                    await delayTask.ConfigureAwait(false);
                    TriggerCheck();
                }
                else
                {
                    await scheduleChangedTask.ConfigureAwait(false);
                    while (_scheduleChangedChannel.Reader.TryRead(out _)) { }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<TimeSpan> GetConfiguredIntervalAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var intervalHours = await db.AppConfig
            .Where(config => config.Id == AppConfigConstants.SingletonId)
            .Select(config => config.ReleaseWatcherIntervalHours)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        intervalHours = intervalHours <= 0
            ? AppConfigConstants.DefaultReleaseWatcherIntervalHours
            : Math.Clamp(intervalHours, 1, 168);

        return TimeSpan.FromHours(intervalHours);
    }

    public async Task CheckReleasesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting release watcher scan...");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var trackedSeries = await db.TrackedSeries
            .Include(ts => ts.Bookmark)
            .Where(ts => !ts.Bookmark.IsDeleted)
            .ToListAsync(ct);
        trackedSeries = trackedSeries
            .Where(ts => ts.NextCheckAt is null || ts.NextCheckAt <= DateTimeOffset.UtcNow)
            .ToList();

        if (trackedSeries.Count == 0)
        {
            _logger.LogInformation("No active tracked series to check.");
            return;
        }

        _logger.LogInformation("Checking {Count} tracked series...", trackedSeries.Count);

        foreach (var series in trackedSeries)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                await CheckAndUpdateSeriesAsync(db, series, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check release for series {SeriesId} ({Title})", series.Id, series.Bookmark.Title);
                lock (_statusLock)
                {
                    _lastError = ex.Message;
                    _failureCount++;
                }
            }
        }

        lock (_statusLock)
        {
            _successCount++;
        }
    }

    public async Task<bool> CheckAndUpdateSeriesAsync(AppDbContext db, TrackedSeries series, CancellationToken ct)
    {
        var provider = _registry.FindByName(series.Provider);
        if (provider == null || !_registry.IsProviderEnabled(series.Provider))
        {
            _logger.LogWarning("Provider {Provider} not found or disabled for series {Title}.", series.Provider, series.Bookmark?.Title);
            return false;
        }

        LibraryReleaseInfo? releaseInfo;
        try
        {
            releaseInfo = await provider.GetLatestReleaseAsync(series.ProviderId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RecordSeriesFailureAsync(db, series, ex.Message, ct).ConfigureAwait(false);
            throw;
        }

        if (releaseInfo is null)
        {
            await RecordSeriesFailureAsync(
                    db,
                    series,
                    "Provider returned no release information.",
                    ct)
                .ConfigureAwait(false);
            return false;
        }

        var changed = false;

        if (IsNewerChapter(releaseInfo.LatestChapter, series.LatestKnownChapter))
        {
            changed = true;

            var releaseEvent = new ReleaseEvent
            {
                Id = Guid.NewGuid(),
                TrackedSeriesId = series.Id,
                Chapter = releaseInfo.LatestChapter ?? "Unknown",
                Volume = releaseInfo.LatestVolume,
                ReleasedAt = releaseInfo.LastReleaseAt ?? DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                Url = releaseInfo.SourceUrl
            };

            db.ReleaseEvents.Add(releaseEvent);

            series.LatestKnownChapter = releaseInfo.LatestChapter;
            series.LastReleaseAt = releaseInfo.LastReleaseAt ?? DateTimeOffset.UtcNow;
            series.LatestChapterUrl = releaseInfo.SourceUrl;
        }

        series.LastChecked = DateTimeOffset.UtcNow;
        series.ConsecutiveFailureCount = 0;
        series.NextCheckAt = null;
        series.LastCheckError = null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (changed)
            await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync().ConfigureAwait(false);

        return changed;
    }

    private static async Task RecordSeriesFailureAsync(
        AppDbContext db,
        TrackedSeries series,
        string error,
        CancellationToken cancellationToken)
    {
        series.LastChecked = DateTimeOffset.UtcNow;
        series.ConsecutiveFailureCount++;
        var backoffHours = Math.Min(
            MaximumBackoffHours,
            Math.Pow(2, Math.Min(series.ConsecutiveFailureCount - 1, 10)));
        series.NextCheckAt = DateTimeOffset.UtcNow.AddHours(backoffHours);
        series.LastCheckError = error;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsNewerChapter(string? newCh, string? oldCh)
    {
        if (string.IsNullOrWhiteSpace(newCh))
            return false;
        if (string.IsNullOrWhiteSpace(oldCh))
            return true;

        if (newCh.Equals(oldCh, StringComparison.OrdinalIgnoreCase))
            return false;

        if (double.TryParse(newCh, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var newNum) &&
            double.TryParse(oldCh, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var oldNum))
        {
            return newNum > oldNum;
        }

        return string.Compare(newCh, oldCh, StringComparison.OrdinalIgnoreCase) > 0;
    }
}
