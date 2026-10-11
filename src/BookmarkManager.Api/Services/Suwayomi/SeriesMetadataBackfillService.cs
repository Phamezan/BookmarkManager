using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>
/// Periodically fills <see cref="SeriesMetadata"/> for titles that lack a fresh entry: every title in
/// the user's Suwayomi library plus every Discover series, newest-updated Discover series first.
/// Lookups run one at a time through <see cref="SeriesMetadataService"/> (the shared AniList limiter
/// paces them), capped per run. When a lookup resolves a country of origin, matching Unknown Discover
/// rows are re-typed so they show under the Manhwa/Manhua/Manga chips.
/// </summary>
public sealed class SeriesMetadataBackfillService : BackgroundService
{
    internal const int MaxLookupsPerRun = 1200;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SeriesMetadataBackfillService> _logger;

    public SeriesMetadataBackfillService(
        IServiceScopeFactory scopeFactory,
        ILogger<SeriesMetadataBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Series metadata backfill service started.");

        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
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
                _logger.LogError(ex, "Series metadata backfill run failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs one backfill pass. Internal so a test can drive a single run.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<ISuwayomiClient>();
        var metadata = scope.ServiceProvider.GetRequiredService<SeriesMetadataService>();

        var titles = new List<string>();
        try
        {
            var library = await client.GetLibraryAsync(ct).ConfigureAwait(false);
            titles.AddRange(library.Select(manga => manga.Title));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Series metadata backfill could not read the Suwayomi library.");
        }

        var discoverSeries = await db.DiscoverSeriesSet
            .AsNoTracking()
            .ToListAsync(ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var allKeys = titles
            .Concat(discoverSeries.Select(s => s.Title))
            .Select(MediaTitleNormalizer.NormalizeForSearch)
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var freshKeys = allKeys.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await db.SeriesMetadataSet
                    .AsNoTracking()
                    .Where(m => allKeys.Contains(m.NormalizedKey))
                    .ToListAsync(ct).ConfigureAwait(false))
                .Where(row => SeriesMetadataService.IsFresh(row, now))
                .Select(row => row.NormalizedKey)
                .ToHashSet(StringComparer.Ordinal);

        var pending = SelectCandidates(titles, discoverSeries, freshKeys, MaxLookupsPerRun);
        if (pending.Count == 0)
        {
            return;
        }

        var typeByKey = new Dictionary<string, DiscoverSeriesType>(StringComparer.Ordinal);
        foreach (var candidate in pending)
        {
            ct.ThrowIfCancellationRequested();
            var row = await metadata.GetOrLookupAsync(candidate.Title, ct).ConfigureAwait(false);
            if (row is null || !row.Found)
            {
                continue;
            }

            var type = DiscoverTypeResolver.FromCountry(row.CountryOfOrigin);
            if (type != DiscoverSeriesType.Unknown)
            {
                typeByKey[candidate.Key] = type;
            }
        }

        if (typeByKey.Count == 0)
        {
            return;
        }

        var updateKeys = typeByKey.Keys.ToList();
        var updates = await db.DiscoverSeriesSet
            .Where(s => s.Type == DiscoverSeriesType.Unknown && updateKeys.Contains(s.TitleKey))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var series in updates)
        {
            series.Type = typeByKey[series.TitleKey];
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Series metadata backfill: {Lookups} lookups, {Retyped} Discover rows re-typed.", pending.Count, updates.Count);
    }

    /// <summary>
    /// Picks the titles to look up: every library title first, then every Discover series by
    /// <see cref="DiscoverSeries.LatestChapterAt"/> descending (current week first). A title whose
    /// normalized key is already fresh is skipped, and duplicates (library and Discover naming the
    /// same series) keep the first occurrence. Capped at <paramref name="max"/>.
    /// </summary>
    internal static IReadOnlyList<(string Title, string Key)> SelectCandidates(
        IReadOnlyList<string> libraryTitles,
        IReadOnlyList<DiscoverSeries> discoverSeries,
        IReadOnlySet<string> freshKeys,
        int max)
    {
        var result = new List<(string Title, string Key)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Consider(string title)
        {
            var key = MediaTitleNormalizer.NormalizeForSearch(title);
            if (key.Length == 0 || freshKeys.Contains(key) || !seen.Add(key))
            {
                return;
            }

            result.Add((title, key));
        }

        foreach (var title in libraryTitles)
        {
            Consider(title);
        }

        foreach (var series in discoverSeries.OrderByDescending(s => s.LatestChapterAt))
        {
            Consider(series.Title);
        }

        return result.Count <= max ? result : result.Take(max).ToList();
    }
}
