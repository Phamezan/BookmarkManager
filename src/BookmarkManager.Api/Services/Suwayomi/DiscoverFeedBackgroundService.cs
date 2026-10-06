using System.Collections.Concurrent;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>Per-source outcome of a Discover run, surfaced by the status endpoint.</summary>
public sealed record DiscoverSourceStatus(string Name, string State, string? Detail);

/// <summary>
/// Builds and refreshes the Discover feed: for each configured source it resolves the "latest
/// Action" filters, pages the listing, fetches chapters for new/updated series, guards on the
/// Action genre, then merges everything into <see cref="DiscoverSeries"/>. The same title from
/// several sources becomes one series with several source entries. A failing source never fails
/// the run.
/// </summary>
public sealed class DiscoverFeedBackgroundService : BackgroundService
{
    private const int SourceConcurrency = 3;
    private static readonly TimeSpan ChapterRefreshInterval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<SuwayomiOptions> _options;
    private readonly ILogger<DiscoverFeedBackgroundService> _logger;

    private readonly object _statusLock = new();
    private bool _running;
    private DateTime? _lastRunAt;
    private IReadOnlyList<DiscoverSourceStatus> _sourceStatuses = [];
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<SuwayomiFilter>>> _filterCache = new();

    public DiscoverFeedBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<SuwayomiOptions> options,
        ILogger<DiscoverFeedBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    public bool IsRunning
    {
        get
        {
            lock (_statusLock)
            {
                return _running;
            }
        }
    }

    public DateTime? LastRunAt
    {
        get
        {
            lock (_statusLock)
            {
                return _lastRunAt;
            }
        }
    }

    public IReadOnlyList<DiscoverSourceStatus> GetSourceStatuses()
    {
        lock (_statusLock)
        {
            return _sourceStatuses;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Discover feed background service started.");

        // First run shortly after startup so the page has data without waiting a full interval.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discover feed run failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _options.Value.DiscoverRefreshMinutes)), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs one full refresh. Internal (not private) so a test can drive a single run.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        lock (_statusLock)
        {
            if (_running)
            {
                return;
            }

            _running = true;
        }

        try
        {
            await RunCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_statusLock)
            {
                _running = false;
            }
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        var options = _options.Value;
        var now = DateTime.UtcNow;
        var retentionFloor = now.AddDays(-Math.Max(1, options.DiscoverRetentionDays));
        _filterCache.Clear();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<ISuwayomiClient>();

        SuwayomiServerStatus server;
        try
        {
            server = await client.GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discover feed skipped: Suwayomi is not reachable.");
            SetStatuses([new DiscoverSourceStatus("*", "error", "Suwayomi is not reachable.")]);
            return;
        }

        var sourceIds = BuildSourceLookup(server.Sources, options.DiscoverSources);

        bool hasSeries = await db.DiscoverSeriesSet.AsNoTracking().AnyAsync(ct).ConfigureAwait(false);
        var cutoff = _lastRunAt is { } previous
            ? previous.AddDays(-1)
            : now.AddDays(-Math.Max(1, options.DiscoverBackfillDays));
        if (!hasSeries)
        {
            cutoff = now.AddDays(-Math.Max(1, options.DiscoverBackfillDays));
        }

        var knownRows = await db.DiscoverSourceEntries
            .AsNoTracking()
            .Select(e => new
            {
                e.MangaId,
                e.LastListedRank,
                e.ChaptersFetchedAt,
                LatestChapterAt = e.Series!.LatestChapterAt
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var known = knownRows.ToDictionary(
            r => r.MangaId,
            r => new KnownEntry(r.LastListedRank, r.ChaptersFetchedAt, r.LatestChapterAt));

        var throttle = new SuwayomiSourceThrottle(options.ThrottleMillisecondsPerSource);
        var results = new ConcurrentBag<SourceRunResult>();

        var configured = options.DiscoverSources
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await Parallel.ForEachAsync(
            configured,
            new ParallelOptions { MaxDegreeOfParallelism = SourceConcurrency, CancellationToken = ct },
            async (sourceName, token) =>
            {
                if (!sourceIds.TryGetValue(sourceName, out var sourceId))
                {
                    results.Add(new SourceRunResult(sourceName, "skipped", "source is not installed", []));
                    return;
                }

                var result = await ProcessSourceSafeAsync(
                    client, sourceName, sourceId, known, cutoff, now, throttle, token).ConfigureAwait(false);
                results.Add(result);
            }).ConfigureAwait(false);

        var ordered = results
            .OrderBy(r => configured.FindIndex(n => string.Equals(n, r.SourceName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var mergeErrors = await MergeAsync(db, ordered, now, retentionFloor, ct).ConfigureAwait(false);
        var statuses = ordered
            .Select(r => mergeErrors.TryGetValue(r.SourceName, out var mergeError)
                ? new DiscoverSourceStatus(r.SourceName, "error", mergeError)
                : new DiscoverSourceStatus(r.SourceName, r.State, r.Detail))
            .ToList();

        await CleanupAsync(db, retentionFloor, ct).ConfigureAwait(false);

        lock (_statusLock)
        {
            _lastRunAt = now;
            _sourceStatuses = statuses;
        }

        var kept = ordered.Sum(r => r.Candidates.Count);
        _logger.LogInformation(
            "Discover feed run completed: {Sources} sources, {Kept} candidates, statuses {Statuses}.",
            configured.Count, kept, string.Join(",", statuses.Select(s => $"{s.Name}:{s.State}")));
    }

    private async Task<SourceRunResult> ProcessSourceSafeAsync(
        ISuwayomiClient client,
        string sourceName,
        string sourceId,
        IReadOnlyDictionary<int, KnownEntry> known,
        DateTime cutoff,
        DateTime now,
        SuwayomiSourceThrottle throttle,
        CancellationToken ct)
    {
        try
        {
            return await ProcessSourceAsync(client, sourceName, sourceId, known, cutoff, now, throttle, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discover source {Source} failed.", sourceName);
            return new SourceRunResult(sourceName, "error", ex.Message, []);
        }
    }

    private async Task<SourceRunResult> ProcessSourceAsync(
        ISuwayomiClient client,
        string sourceName,
        string sourceId,
        IReadOnlyDictionary<int, KnownEntry> known,
        DateTime cutoff,
        DateTime now,
        SuwayomiSourceThrottle throttle,
        CancellationToken ct)
    {
        var maxPages = Math.Max(1, _options.Value.DiscoverMaxPagesPerSource);

        var filters = await GetFiltersAsync(client, sourceName, sourceId, throttle, ct).ConfigureAwait(false);
        var plan = DiscoverFilterResolver.Resolve(filters);
        if (!plan.CanRun)
        {
            _logger.LogWarning("Discover source {Source} skipped: {Reason}.", sourceName, plan.SkipReason);
            return new SourceRunResult(sourceName, "skipped", plan.SkipReason, []);
        }

        var candidates = new List<SourceMangaCandidate>();
        var rank = 0;
        for (var page = 1; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            await throttle.WaitAsync(sourceName, ct).ConfigureAwait(false);
            var listing = await client.FetchSourceMangaAsync(sourceId, plan.Changes, page, ct).ConfigureAwait(false);
            if (listing.Mangas.Count == 0)
            {
                break;
            }

            var wholePageKnownOld = true;
            foreach (var manga in listing.Mangas)
            {
                var currentRank = rank++;
                known.TryGetValue(manga.Id, out var entry);

                var isKnownOld = entry is not null && entry.LatestChapterAt < cutoff;
                if (!isKnownOld)
                {
                    wholePageKnownOld = false;
                }

                var improved = entry is null || entry.LastListedRank is null || currentRank < entry.LastListedRank.Value;
                var stale = entry is not null
                    && (entry.ChaptersFetchedAt is null || entry.ChaptersFetchedAt.Value < now - ChapterRefreshInterval);
                var needsChapters = entry is null || improved || stale;

                if (!needsChapters)
                {
                    candidates.Add(new SourceMangaCandidate(string.Empty, [], DiscoverSeriesType.Unknown, manga.Id, currentRank, false, []));
                    continue;
                }

                SuwayomiMangaDetails details;
                try
                {
                    await throttle.WaitAsync(sourceName, ct).ConfigureAwait(false);
                    details = await client.GetMangaDetailsAsync(manga.Id, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Discover chapter fetch failed for manga {MangaId} on {Source}.", manga.Id, sourceName);
                    continue;
                }

                if (!ContainsAction(details.Genres))
                {
                    continue;
                }

                candidates.Add(new SourceMangaCandidate(
                    details.Title,
                    details.Genres,
                    DiscoverTypeResolver.Derive(details.Genres),
                    manga.Id,
                    currentRank,
                    true,
                    details.Chapters));
            }

            if (wholePageKnownOld || !listing.HasNextPage)
            {
                break;
            }
        }

        return new SourceRunResult(sourceName, "ok", null, candidates);
    }

    private async Task<IReadOnlyList<SuwayomiFilter>> GetFiltersAsync(
        ISuwayomiClient client,
        string sourceName,
        string sourceId,
        SuwayomiSourceThrottle throttle,
        CancellationToken ct)
    {
        var task = _filterCache.GetOrAdd(sourceName, _ => FetchAsync());
        return await task.ConfigureAwait(false);

        async Task<IReadOnlyList<SuwayomiFilter>> FetchAsync()
        {
            await throttle.WaitAsync(sourceName, ct).ConfigureAwait(false);
            return await client.GetSourceFiltersAsync(sourceId, ct).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, string>> MergeAsync(
        AppDbContext db,
        IReadOnlyList<SourceRunResult> results,
        DateTime now,
        DateTime retentionFloor,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var seriesByKey = (await db.DiscoverSeriesSet.ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(s => s.TitleKey, StringComparer.Ordinal);
        var entriesByManga = (await db.DiscoverSourceEntries.ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(e => e.MangaId);
        var seriesById = seriesByKey.Values.ToDictionary(s => s.Id);

        var fetchedMangaIds = results
            .SelectMany(r => r.Candidates)
            .Where(c => c.FetchedChapters)
            .Select(c => c.MangaId)
            .Distinct()
            .ToList();

        var chapterIndex = fetchedMangaIds.Count == 0
            ? new Dictionary<(int MangaId, int SourceOrder), DiscoverChapter>()
            : (await db.DiscoverChapters.Where(c => fetchedMangaIds.Contains(c.MangaId)).ToListAsync(ct)
                .ConfigureAwait(false))
                .ToDictionary(c => (c.MangaId, c.SourceOrder));

        var touchedSeries = new HashSet<DiscoverSeries>();

        foreach (var result in results)
        {
            try
            {
                foreach (var candidate in result.Candidates)
                {
                    if (!entriesByManga.TryGetValue(candidate.MangaId, out var entry))
                    {
                        if (!candidate.FetchedChapters || string.IsNullOrWhiteSpace(candidate.Title))
                        {
                            continue;
                        }

                        var titleKey = MediaTitleNormalizer.NormalizeForSearch(candidate.Title);
                        if (string.IsNullOrWhiteSpace(titleKey))
                        {
                            continue;
                        }

                        if (!seriesByKey.TryGetValue(titleKey, out var series))
                        {
                            series = new DiscoverSeries
                            {
                                Id = Guid.NewGuid(),
                                TitleKey = titleKey,
                                Title = candidate.Title,
                                Type = candidate.Type,
                                Genres = string.Join(",", candidate.Genres),
                                LatestChapterAt = DateTime.MinValue
                            };
                            db.DiscoverSeriesSet.Add(series);
                            seriesByKey[titleKey] = series;
                            seriesById[series.Id] = series;
                        }

                        entry = new DiscoverSourceEntry
                        {
                            Id = Guid.NewGuid(),
                            SeriesId = series.Id,
                            SourceName = result.SourceName,
                            MangaId = candidate.MangaId
                        };
                        db.DiscoverSourceEntries.Add(entry);
                        entriesByManga[candidate.MangaId] = entry;
                    }

                    if (!seriesById.TryGetValue(entry.SeriesId, out var series2))
                    {
                        continue;
                    }

                    touchedSeries.Add(series2);
                    entry.LastListedRank = candidate.Rank;

                    if (!candidate.FetchedChapters)
                    {
                        continue;
                    }

                    entry.ChaptersFetchedAt = now;
                    if (!string.IsNullOrWhiteSpace(candidate.Genres.FirstOrDefault()))
                    {
                        series2.Genres = string.Join(",", candidate.Genres);
                    }

                    if (series2.Type == DiscoverSeriesType.Unknown)
                    {
                        series2.Type = candidate.Type;
                    }

                    foreach (var chapter in candidate.Chapters)
                    {
                        if (chapter.UploadedAt is not { } uploadedAt || uploadedAt < retentionFloor)
                        {
                            continue;
                        }

                        var key = (candidate.MangaId, chapter.SourceOrder);
                        if (chapterIndex.TryGetValue(key, out var row))
                        {
                            row.SeriesId = series2.Id;
                            row.Name = chapter.Name;
                            row.ChapterNumber = chapter.ChapterNumber;
                            row.UploadedAt = uploadedAt;
                            row.SourceName = result.SourceName;
                        }
                        else
                        {
                            row = new DiscoverChapter
                            {
                                Id = Guid.NewGuid(),
                                SeriesId = series2.Id,
                                MangaId = candidate.MangaId,
                                SourceName = result.SourceName,
                                ChapterNumber = chapter.ChapterNumber,
                                Name = chapter.Name,
                                UploadedAt = uploadedAt,
                                SourceOrder = chapter.SourceOrder
                            };
                            db.DiscoverChapters.Add(row);
                            chapterIndex[key] = row;
                        }

                        if (uploadedAt > series2.LatestChapterAt)
                        {
                            series2.LatestChapterAt = uploadedAt;
                        }
                    }
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Discover merge for source {Source} failed.", result.SourceName);
                errors[result.SourceName] = ex.Message;
            }
        }

        await RecomputeCoversAsync(db, touchedSeries, now, ct).ConfigureAwait(false);
        return errors;
    }

    private static async Task RecomputeCoversAsync(
        AppDbContext db, HashSet<DiscoverSeries> touchedSeries, DateTime now, CancellationToken ct)
    {
        if (touchedSeries.Count == 0)
        {
            return;
        }

        var seriesIds = touchedSeries.Select(s => s.Id).ToList();
        var chapterTimes = await db.DiscoverChapters
            .Where(c => seriesIds.Contains(c.SeriesId))
            .Select(c => new { c.SeriesId, c.MangaId, c.UploadedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        var coverBySeries = chapterTimes
            .GroupBy(c => c.SeriesId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.UploadedAt).First().MangaId);

        foreach (var series in touchedSeries)
        {
            if (coverBySeries.TryGetValue(series.Id, out var mangaId))
            {
                series.CoverMangaId = mangaId;
            }

            series.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task CleanupAsync(AppDbContext db, DateTime retentionFloor, CancellationToken ct)
    {
        await db.DiscoverChapters
            .Where(c => c.UploadedAt < retentionFloor)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        await db.DiscoverSeriesSet
            .Where(s => !db.DiscoverChapters.Any(c => c.SeriesId == s.Id))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private void SetStatuses(IReadOnlyList<DiscoverSourceStatus> statuses)
    {
        lock (_statusLock)
        {
            _sourceStatuses = statuses;
        }
    }

    private static bool ContainsAction(IReadOnlyList<string> genres)
        => genres.Any(g => string.Equals(g, "Action", StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyDictionary<string, string> BuildSourceLookup(
        IReadOnlyList<SuwayomiSource> sources, IReadOnlyList<string> names)
    {
        var english = sources
            .Where(s => string.Equals(s.Lang, "en", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var match = english.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                lookup[name] = match.Id;
            }
        }

        return lookup;
    }

    private sealed record KnownEntry(int? LastListedRank, DateTime? ChaptersFetchedAt, DateTime LatestChapterAt);

    private sealed record SourceMangaCandidate(
        string Title,
        IReadOnlyList<string> Genres,
        DiscoverSeriesType Type,
        int MangaId,
        int Rank,
        bool FetchedChapters,
        IReadOnlyList<SuwayomiChapterDetails> Chapters);

    private sealed record SourceRunResult(
        string SourceName, string State, string? Detail, IReadOnlyList<SourceMangaCandidate> Candidates);
}
