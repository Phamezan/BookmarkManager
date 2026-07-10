using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Library;

/// <summary>
/// Populates and refreshes <see cref="LibraryCatalogEntry"/> - the local mirror the Library "Browse"
/// view pages through - using a durable, crash-resumable work queue (Queue-Based Load Leveling): each
/// "fetch the next page" step is a row in <see cref="LibraryCatalogSyncQueue"/> rather than in-memory
/// state, so a container restart mid-crawl resumes exactly where it left off. One worker loop runs per
/// <see cref="IBulkCatalogProvider"/>, and each loop's throughput is governed entirely by that
/// provider's existing <see cref="ProviderBudgetTracker"/>-tracked rate limiter (shared with live
/// search/trending calls), so the crawl never exceeds the same safe request budget interactive calls do.
/// </summary>
public sealed class LibraryCatalogSyncBackgroundService : BackgroundService
{
    private const int MaxAttempts = 5;
    private const int DefaultTopUpPages = 2;
    private static readonly TimeSpan TopUpInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan EmptyQueuePollDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LibraryCatalogSyncBackgroundService> _logger;
    private readonly LibraryProviderRegistry _registry;
    private readonly Channel<bool> _resyncChannel = Channel.CreateUnbounded<bool>();
    private readonly object _statusLock = new();
    private bool _isCrawling;

    public LibraryCatalogSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<LibraryCatalogSyncBackgroundService> logger,
        LibraryProviderRegistry registry)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _registry = registry;
    }

    /// <summary>Forces a fresh, unbounded ground-up crawl of every sequence, ignoring any prior progress.</summary>
    public void TriggerFullResync() => _resyncChannel.Writer.TryWrite(true);

    public async Task<LibraryCatalogSyncStatusDto> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var totalEntries = await db.LibraryCatalogEntries.CountAsync(cancellationToken).ConfigureAwait(false);
        var pending = await db.LibraryCatalogSyncQueue.CountAsync(q => q.Status == CatalogSyncQueueStatus.Pending, cancellationToken).ConfigureAwait(false);
        var processing = await db.LibraryCatalogSyncQueue.CountAsync(q => q.Status == CatalogSyncQueueStatus.Processing, cancellationToken).ConfigureAwait(false);
        var failed = await db.LibraryCatalogSyncQueue.CountAsync(q => q.Status == CatalogSyncQueueStatus.Failed, cancellationToken).ConfigureAwait(false);
        // SQLite's EF Core provider can't translate ORDER BY/MAX over DateTimeOffset columns, so the
        // max is computed client-side over just the timestamp column (not full rows).
        var refreshTimestamps = await db.LibraryCatalogEntries
            .Select(e => e.LastRefreshedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var lastRefreshedAt = refreshTimestamps.Count > 0 ? refreshTimestamps.Max() : (DateTimeOffset?)null;

        bool isCrawling;
        lock (_statusLock)
        {
            isCrawling = _isCrawling;
        }

        return new LibraryCatalogSyncStatusDto
        {
            TotalEntries = totalEntries,
            PendingQueueCount = pending,
            ProcessingQueueCount = processing,
            FailedQueueCount = failed,
            IsCrawling = isCrawling || pending > 0 || processing > 0,
            LastRefreshedAt = lastRefreshedAt
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Library catalog sync background service started.");

        try
        {
            await EnsureQueueSeededAsync(forceFullUnboundedCrawl: false, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed catalog sync queue on startup.");
        }

        var loops = GetBulkProviders()
            .Select(provider => RunProviderLoopAsync(provider, stoppingToken))
            .ToArray();

        var topUpTimer = RunTopUpTimerAsync(stoppingToken);

        await Task.WhenAll(loops.Append(topUpTimer)).ConfigureAwait(false);
    }

    private IEnumerable<IBulkCatalogProvider> GetBulkProviders() =>
        _registry.AllProviders.OfType<IBulkCatalogProvider>();

    private async Task RunTopUpTimerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var delayTask = Task.Delay(TopUpInterval, waitCts.Token);
            var resyncTask = _resyncChannel.Reader.ReadAsync(waitCts.Token).AsTask();

            Task completed;
            try
            {
                completed = await Task.WhenAny(delayTask, resyncTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            waitCts.Cancel();

            var fullResync = completed == resyncTask;
            if (fullResync)
            {
                while (_resyncChannel.Reader.TryRead(out _)) { }
            }

            try
            {
                await EnsureQueueSeededAsync(forceFullUnboundedCrawl: fullResync, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed catalog sync queue.");
            }
        }
    }

    /// <summary>Seeds one pending item per (provider, sequence) that doesn't already have an
    /// active (pending/processing) item. Unbounded (full crawl) when explicitly requested, or when a
    /// provider has zero catalog rows yet (fresh install); otherwise a small bounded top-up.
    /// Internal (not private) so unit tests can exercise seeding without running the full hosted-service loop.</summary>
    internal async Task EnsureQueueSeededAsync(bool forceFullUnboundedCrawl, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        foreach (var provider in GetBulkProviders())
        {
            if (!_registry.IsProviderEnabled(provider.ProviderName))
                continue;

            var hasAnyEntries = await db.LibraryCatalogEntries
                .AnyAsync(e => e.Provider == provider.ProviderName, cancellationToken)
                .ConfigureAwait(false);

            foreach (var key in provider.CatalogMediaTypeQueries)
            {
                var hasActive = await db.LibraryCatalogSyncQueue
                    .AnyAsync(
                        q => q.Provider == provider.ProviderName &&
                             q.MediaTypeQuery == key &&
                             (q.Status == CatalogSyncQueueStatus.Pending || q.Status == CatalogSyncQueueStatus.Processing),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (hasActive)
                    continue;

                int? budget = forceFullUnboundedCrawl || !hasAnyEntries ? null : DefaultTopUpPages;

                db.LibraryCatalogSyncQueue.Add(new LibraryCatalogSyncQueueItem
                {
                    Id = Guid.NewGuid(),
                    Provider = provider.ProviderName,
                    MediaTypeQuery = key,
                    ContinuationToken = null,
                    RemainingPages = budget,
                    Status = CatalogSyncQueueStatus.Pending,
                    CreatedAt = now
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunProviderLoopAsync(IBulkCatalogProvider provider, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var item = await ClaimNextPendingItemAsync(provider.ProviderName, stoppingToken).ConfigureAwait(false);

            if (item is null)
            {
                lock (_statusLock) { _isCrawling = false; }
                try
                {
                    await Task.Delay(EmptyQueuePollDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            lock (_statusLock) { _isCrawling = true; }

            try
            {
                await ProcessQueueItemAsync(provider, item, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Catalog sync item {ItemId} ({Provider}/{Key}) failed unexpectedly.", item.Id, provider.ProviderName, item.MediaTypeQuery);
                await RequeueWithBackoffAsync(item.Id, ex.Message, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Atomically claims the oldest due Pending item for <paramref name="providerName"/>, marking it
    /// Processing. Internal (not private) so unit tests can exercise the claim query - including its SQLite
    /// DateTimeOffset-translation workaround - directly, without spinning up the always-on polling loop.</summary>
    internal async Task<LibraryCatalogSyncQueueItem?> ClaimNextPendingItemAsync(string providerName, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        // SQLite's EF Core provider can't translate relational (<=/>=) comparisons or ORDER BY over
        // DateTimeOffset columns, so Pending rows for this provider are materialized on a translatable
        // predicate first, then the NextAttemptAt/backoff filter and CreatedAt ordering happen client-side.
        var candidates = await db.LibraryCatalogSyncQueue
            .Where(q => q.Provider == providerName && q.Status == CatalogSyncQueueStatus.Pending)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var item = candidates
            .Where(q => q.NextAttemptAt is null || q.NextAttemptAt <= now)
            .OrderBy(q => q.CreatedAt)
            .FirstOrDefault();

        if (item is not null)
        {
            item.Status = CatalogSyncQueueStatus.Processing;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return item;
    }

    /// <summary>Internal (not private) so unit tests can process a single queue item deterministically
    /// without spinning up the always-on <see cref="RunProviderLoopAsync"/> polling loop.</summary>
    internal async Task ProcessQueueItemAsync(IBulkCatalogProvider provider, LibraryCatalogSyncQueueItem item, CancellationToken ct)
    {
        CatalogPageResult page;
        try
        {
            page = await provider.GetCatalogPageAsync(item.MediaTypeQuery, item.ContinuationToken, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RequeueWithBackoffAsync(item.Id, ex.Message, ct).ConfigureAwait(false);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await UpsertEntriesAsync(db, provider.ProviderName, page.Entries, page.RankBase, ct).ConfigureAwait(false);

        var current = await db.LibraryCatalogSyncQueue.FirstAsync(q => q.Id == item.Id, ct).ConfigureAwait(false);
        current.Status = CatalogSyncQueueStatus.Done;

        var noForwardProgress = page.NextContinuationToken is not null && page.NextContinuationToken == current.ContinuationToken;
        var budgetExhausted = current.RemainingPages is 0;

        if (page.NextContinuationToken is not null && !noForwardProgress && !budgetExhausted)
        {
            db.LibraryCatalogSyncQueue.Add(new LibraryCatalogSyncQueueItem
            {
                Id = Guid.NewGuid(),
                Provider = provider.ProviderName,
                MediaTypeQuery = current.MediaTypeQuery,
                ContinuationToken = page.NextContinuationToken,
                RemainingPages = current.RemainingPages is { } remaining ? remaining - 1 : null,
                Status = CatalogSyncQueueStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        else if (noForwardProgress)
        {
            _logger.LogWarning(
                "Catalog sync sequence {Provider}/{Key} made no forward progress from token {Token}; stopping this chain to avoid looping.",
                provider.ProviderName, current.MediaTypeQuery, current.ContinuationToken);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task UpsertEntriesAsync(
        AppDbContext db,
        string provider,
        IReadOnlyList<LibraryEntryDto> entries,
        int? rankBase,
        CancellationToken ct)
    {
        if (entries.Count == 0)
            return;

        var providerIds = entries.Select(e => e.ProviderId).ToList();
        var existing = await db.LibraryCatalogEntries
            .Where(e => e.Provider == provider && providerIds.Contains(e.ProviderId))
            .ToDictionaryAsync(e => e.ProviderId, ct)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < entries.Count; i++)
        {
            var dto = entries[i];
            var rank = rankBase is { } b ? b + i : (int?)null;

            if (existing.TryGetValue(dto.ProviderId, out var row))
            {
                ApplyDto(row, dto);
                row.LastRefreshedAt = now;
                if (rank is not null)
                    row.PopularityRank = rank;
            }
            else
            {
                row = new LibraryCatalogEntry
                {
                    Id = Guid.NewGuid(),
                    Provider = provider,
                    ProviderId = dto.ProviderId,
                    FirstImportedAt = now,
                    LastRefreshedAt = now,
                    PopularityRank = rank
                };
                ApplyDto(row, dto);
                db.LibraryCatalogEntries.Add(row);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static void ApplyDto(LibraryCatalogEntry row, LibraryEntryDto dto)
    {
        row.Title = dto.Title;
        row.AlternateTitles = LibraryCatalogEntry.JoinList(dto.AlternateTitles);
        row.Authors = LibraryCatalogEntry.JoinList(dto.Authors);
        row.MediaType = dto.MediaType;
        row.CoverImageUrl = dto.CoverImageUrl;
        row.Synopsis = dto.Synopsis;
        row.Genres = LibraryCatalogEntry.JoinList(dto.Genres);
        row.Rating = dto.Rating;
        row.Status = dto.Status;
        row.LatestChapter = dto.LatestChapter;
        row.LatestVolume = dto.LatestVolume;
        row.LastReleaseAt = dto.LastReleaseAt;
        row.SourceUrl = dto.SourceUrl;
    }

    internal async Task RequeueWithBackoffAsync(Guid itemId, string error, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.LibraryCatalogSyncQueue.FirstOrDefaultAsync(q => q.Id == itemId, ct).ConfigureAwait(false);
        if (row is null)
            return;

        row.Attempts++;
        row.LastError = error;

        if (row.Attempts >= MaxAttempts)
        {
            row.Status = CatalogSyncQueueStatus.Failed;
        }
        else
        {
            row.Status = CatalogSyncQueueStatus.Pending;
            row.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, row.Attempts) * 5);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
