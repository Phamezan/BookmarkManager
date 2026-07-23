using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Embedding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Library;

/// <summary>
/// Background pass that fills in embeddings the interactive sync path missed: rows crawled before the
/// ONNX model finished downloading, rows whose embed text changed while embeddings were unavailable, and
/// the whole existing catalog on the first boot after this feature ships. Scans the catalog in batches of
/// <see cref="EmbeddingConstants.BackfillBatchSize"/>, re-embedding any row whose <see cref="LibraryEmbeddingText"/>
/// hash no longer matches its stored <see cref="LibraryCatalogEntry.EmbeddingSourceHash"/> (or that has no
/// embedding yet). Each batch commits independently so a restart resumes without redoing finished work, and
/// the whole worker is gated on <see cref="IEmbeddingService.IsReady"/> so it stays idle until the model
/// loads. Complements <see cref="LibraryCatalogSyncBackgroundService"/> - it never blocks the crawl.
/// </summary>
public sealed class LibraryEmbeddingBackfillService : BackgroundService
{
    private static readonly TimeSpan ModelNotReadyPollDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdlePassInterval = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorSearchService _vectorSearch;
    private readonly ILogger<LibraryEmbeddingBackfillService> _logger;

    public LibraryEmbeddingBackfillService(
        IServiceScopeFactory scopeFactory,
        IEmbeddingService embeddingService,
        IVectorSearchService vectorSearch,
        ILogger<LibraryEmbeddingBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _embeddingService = embeddingService;
        _vectorSearch = vectorSearch;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Library embedding backfill service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_embeddingService.IsReady)
            {
                if (!await DelayAsync(ModelNotReadyPollDelay, stoppingToken).ConfigureAwait(false))
                    break;
                continue;
            }

            try
            {
                var embedded = await RunBackfillPassAsync(stoppingToken).ConfigureAwait(false);
                if (embedded > 0)
                    _logger.LogInformation("Embedding backfill pass embedded {Count} catalog entries.", embedded);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding backfill pass failed; will retry after the idle interval.");
            }

            if (!await DelayAsync(IdlePassInterval, stoppingToken).ConfigureAwait(false))
                break;
        }
    }

    /// <summary>Scans the entire catalog once in <see cref="EmbeddingConstants.BackfillBatchSize"/>-sized
    /// batches, embedding every row whose embed text hash is stale or missing, and returns how many rows
    /// were embedded. Internal (not private) so unit tests can drive a single pass deterministically
    /// without spinning up the always-on hosted loop.</summary>
    internal async Task<int> RunBackfillPassAsync(CancellationToken cancellationToken)
    {
        var ids = await LoadAllEntryIdsAsync(cancellationToken).ConfigureAwait(false);

        var total = 0;
        foreach (var batch in ids.Chunk(EmbeddingConstants.BackfillBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += await EmbedBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private async Task<List<Guid>> LoadAllEntryIdsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.LibraryCatalogEntries
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<int> EmbedBatchAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.LibraryCatalogEntries
            .Where(e => ids.Contains(e.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var pending = new List<(LibraryCatalogEntry Row, string Hash)>();
        var texts = new List<string>();
        foreach (var row in rows)
        {
            var text = LibraryEmbeddingText.Build(row);
            var hash = LibraryEmbeddingText.SourceHash(row);
            if (row.Embedding is not null && string.Equals(row.EmbeddingSourceHash, hash, StringComparison.Ordinal))
                continue;

            pending.Add((row, hash));
            texts.Add(text);
        }

        if (pending.Count == 0)
            return 0;

        var vectors = await _embeddingService.EmbedBatchAsync(texts, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < pending.Count; i++)
        {
            pending[i].Row.SetEmbeddingVector(vectors[i]);
            pending[i].Row.EmbeddingSourceHash = pending[i].Hash;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        // See LibraryCatalogSyncBackgroundService.EmbedUpsertedEntriesAsync for why this is needed on top
        // of the vector cache's count-based self-heal: re-embedding an existing row (this backfill pass's
        // whole purpose) leaves the embedded-row count unchanged, so without this explicit invalidation
        // the cache would keep serving the stale vector this pass just replaced.
        _vectorSearch.InvalidateCatalog();
        return pending.Count;
    }

    /// <summary>Delays for <paramref name="delay"/>, returning false if cancellation fired (the caller
    /// should stop) or true if the delay elapsed normally.</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
