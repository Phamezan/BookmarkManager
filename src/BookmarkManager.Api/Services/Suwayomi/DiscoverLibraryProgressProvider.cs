using BookmarkManager.Api.Services.BookmarkTagging;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>Read/latest chapter counts for a series in the user's Suwayomi library.</summary>
public readonly record struct DiscoverProgressValue(int Read, int Latest);

/// <summary>
/// Fetches the user's Suwayomi library reading progress and caches it for 60 s, so a Discover page
/// load never fans out to Suwayomi more than once a minute. Matching is by normalized title
/// (<see cref="MediaTitleNormalizer.NormalizeForSearch"/>) so a series found on several sources still
/// lines up with the library entry.
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
            var library = await client.GetLibraryAsync(ct).ConfigureAwait(false);

            var progress = new Dictionary<string, DiscoverProgressValue>(StringComparer.Ordinal);
            foreach (var manga in library)
            {
                var key = MediaTitleNormalizer.NormalizeForSearch(manga.Title);
                if (key.Length == 0)
                {
                    continue;
                }

                progress[key] = new DiscoverProgressValue(
                    (int)Math.Truncate(manga.LatestReadChapter ?? 0),
                    (int)Math.Truncate(manga.HighestNumberedChapter ?? 0));
            }

            _cache = progress;
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
}
