using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>
/// Periodically fills <see cref="SeriesMetadata"/> for titles that lack a fresh entry: every title in
/// the user's Suwayomi library plus every Discover series whose type is still Unknown. Lookups run one
/// at a time through <see cref="SeriesMetadataService"/> (the shared AniList limiter paces them),
/// capped per run. When a lookup resolves a country of origin, matching Unknown Discover rows are
/// re-typed so they show under the Manhwa/Manhua/Manga chips.
/// </summary>
public sealed class SeriesMetadataBackfillService : BackgroundService
{
    internal const int MaxLookupsPerRun = 300;

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

        var unknownTitles = await db.DiscoverSeriesSet
            .AsNoTracking()
            .Where(s => s.Type == DiscoverSeriesType.Unknown)
            .Select(s => s.Title)
            .ToListAsync(ct).ConfigureAwait(false);
        titles.AddRange(unknownTitles);

        var candidates = titles
            .Select(title => (Title: title, Key: MediaTitleNormalizer.NormalizeForSearch(title)))
            .Where(candidate => candidate.Key.Length > 0)
            .GroupBy(candidate => candidate.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var keys = candidates.Select(candidate => candidate.Key).ToList();
        var freshKeys = (await db.SeriesMetadataSet
                .AsNoTracking()
                .Where(m => keys.Contains(m.NormalizedKey))
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(row => SeriesMetadataService.IsFresh(row, now))
            .Select(row => row.NormalizedKey)
            .ToHashSet(StringComparer.Ordinal);

        var pending = candidates
            .Where(candidate => !freshKeys.Contains(candidate.Key))
            .Take(MaxLookupsPerRun)
            .ToList();
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
}
