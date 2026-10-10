using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>
/// Reading progress plus the identity of the series in the user's Suwayomi library: its manga id,
/// the library series' own title and the display name of the source it belongs to.
/// </summary>
public readonly record struct DiscoverProgressValue(
    int Read, int Latest, int MangaId, string LibraryTitle = "", string? SourceName = null);

/// <summary>
/// Fetches the user's Suwayomi library reading progress and caches it for 60 s, so a Discover page
/// load never fans out to Suwayomi more than once a minute. Matching is by normalized title
/// (<see cref="MediaTitleNormalizer.NormalizeForSearch"/>) so a series found on several sources still
/// lines up with the library entry. The index also carries every alternative title recorded in
/// <see cref="SeriesMetadata.TitlesJson"/>, so a library series whose readable title differs from the
/// Discover title still gets its badge; exact-title keys win over alias keys on a collision.
/// </summary>
public sealed class DiscoverLibraryProgressProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DiscoverLibraryProgressProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, DiscoverProgressValue> _cache =
        new Dictionary<string, DiscoverProgressValue>(StringComparer.Ordinal);
    private DateTime _fetchedAt = DateTime.MinValue;

    public DiscoverLibraryProgressProvider(
        IServiceScopeFactory scopeFactory,
        ILogger<DiscoverLibraryProgressProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, DiscoverProgressValue>> GetAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _fetchedAt < CacheDuration)
        {
            return _cache;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow - _fetchedAt < CacheDuration)
            {
                return _cache;
            }

            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<ISuwayomiClient>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var library = await client.GetLibraryAsync(ct).ConfigureAwait(false);

            var keys = library
                .Select(manga => MediaTitleNormalizer.NormalizeForSearch(manga.Title))
                .Where(key => key.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var metadataByKey = keys.Count == 0
                ? new Dictionary<string, SeriesMetadata>(StringComparer.Ordinal)
                : (await db.SeriesMetadataSet
                        .AsNoTracking()
                        .Where(m => keys.Contains(m.NormalizedKey))
                        .ToListAsync(ct).ConfigureAwait(false))
                    .ToDictionary(m => m.NormalizedKey, StringComparer.Ordinal);

            _cache = BuildIndex(library, metadataByKey);
            _fetchedAt = DateTime.UtcNow;
            return _cache;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discover library progress lookup failed.");
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Builds the normalized-title -> progress index from the library, then adds a key for every
    /// alternative title stored for a library series. Exact-title keys are inserted first and aliases
    /// only fill gaps (<see cref="Dictionary{TKey,TValue}.TryAdd"/>), so an exact match always wins.
    /// </summary>
    internal static IReadOnlyDictionary<string, DiscoverProgressValue> BuildIndex(
        IReadOnlyList<SuwayomiLibraryManga> library,
        IReadOnlyDictionary<string, SeriesMetadata> metadataByKey)
    {
        var index = new Dictionary<string, DiscoverProgressValue>(StringComparer.Ordinal);

        foreach (var manga in library)
        {
            var key = MediaTitleNormalizer.NormalizeForSearch(manga.Title);
            if (key.Length == 0)
            {
                continue;
            }

            index.TryAdd(key, new DiscoverProgressValue(
                (int)Math.Truncate(manga.LatestReadChapter ?? 0),
                (int)Math.Truncate(manga.HighestNumberedChapter ?? 0),
                manga.Id,
                manga.Title,
                manga.SourceDisplayName));
        }

        foreach (var manga in library)
        {
            var key = MediaTitleNormalizer.NormalizeForSearch(manga.Title);
            if (key.Length == 0
                || !index.TryGetValue(key, out var value)
                || !metadataByKey.TryGetValue(key, out var metadata)
                || !metadata.Found)
            {
                continue;
            }

            foreach (var alternate in SeriesMetadataService.ParseTitles(metadata.TitlesJson))
            {
                var aliasKey = MediaTitleNormalizer.NormalizeForSearch(alternate);
                if (aliasKey.Length == 0 || aliasKey == key)
                {
                    continue;
                }

                index.TryAdd(aliasKey, value);
            }
        }

        return index;
    }
}
