using System.Collections.Concurrent;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.BookmarkTagging;

internal sealed partial class AiBookmarkAutoTaggingService
{
    private async Task PrefetchSourceTagsAsync(
        IReadOnlyList<SourceTagLookupRequest> requests,
        ConcurrentDictionary<SourceTagLookupKey, List<ProvenanceTagEntry>> cache,
        ConcurrentDictionary<SourceTagLookupKey, byte> providerFailedKeys,
        AiAutoTagSummaryDto summary,
        bool bypassProviderCache,
        CancellationToken cancellationToken)
    {
        var unique = requests
            .GroupBy(request => request.Key)
            .Select(group => group.First())
            .Where(request => !cache.ContainsKey(request.Key))
            .ToList();

        if (unique.Count == 0)
            return;

        summary.Messages.Add(
            $"Prefetching provider tags for {unique.Count} unique series (up to {ProviderLookupConcurrency} concurrent lookups)...");

        using var semaphore = new SemaphoreSlim(ProviderLookupConcurrency);
        var tasks = unique.Select(request => PrefetchSingleLookupAsync(
            request,
            cache,
            providerFailedKeys,
            summary,
            semaphore,
            bypassProviderCache,
            cancellationToken));

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Keep provider results that finished before cancel; apply pass uses partial cache.
        }
    }

    private async Task PrefetchSingleLookupAsync(
        SourceTagLookupRequest request,
        ConcurrentDictionary<SourceTagLookupKey, List<ProvenanceTagEntry>> cache,
        ConcurrentDictionary<SourceTagLookupKey, byte> providerFailedKeys,
        AiAutoTagSummaryDto summary,
        SemaphoreSlim semaphore,
        bool bypassProviderCache,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.ContainsKey(request.Key))
                return;

            try
            {
                var tags = await FetchSourceTagsAsync(
                        request.Key.Domain,
                        request.CanonicalTitle,
                        request.Url,
                        request.FolderPath,
                        bypassProviderCache,
                        cancellationToken)
                    .ConfigureAwait(false);
                cache[request.Key] = tags;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Bookmark auto-tag provider lookup failed for {Title} ({Domain}).",
                    request.CanonicalTitle,
                    request.Key.Domain);
                cache[request.Key] = [];
                providerFailedKeys[request.Key] = 1;
                lock (summary.Messages)
                {
                    summary.Messages.Add($"Provider lookup failed for '{request.CanonicalTitle}': {ex.Message}");
                }
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<List<ProvenanceTagEntry>> FetchSourceTagsAsync(
        BookmarkTagDomain domain,
        string canonicalTitle,
        string? url,
        string? folderPath,
        bool bypassProviderCache,
        CancellationToken cancellationToken)
    {
        var context = BuildLookupContext(domain, canonicalTitle, url, folderPath, bypassProviderCache);
        List<(ProviderTagResult Result, string ProviderName)> results = domain switch
        {
            BookmarkTagDomain.Anime => await FetchAnimeProviderResultsAsync(context, cancellationToken).ConfigureAwait(false),
            BookmarkTagDomain.Manga => await FetchMangaProviderResultsAsync(context, cancellationToken).ConfigureAwait(false),
            BookmarkTagDomain.Novel => await GetNovelTagsAsync(context, cancellationToken).ConfigureAwait(false),
            _ => []
        };

        return results
            .Where(rp => !rp.Result.WasRejected)
            .SelectMany(rp => rp.Result.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => new ProvenanceTagEntry(tag.Trim(), rp.ProviderName, rp.Result.MatchScore, rp.Result.CanonicalTitle)))
            .DistinctBy(entry => entry.Tag, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<(ProviderTagResult Result, string ProviderName)>> FetchAnimeProviderResultsAsync(
        MediaTagLookupContext context,
        CancellationToken cancellationToken)
    {
        var anilistTask = _anilist.GetTagsForTitleAsync(context, cancellationToken);
        var kitsuTask = _kitsu.GetTagsForTitleAsync(context, cancellationToken);
        await Task.WhenAll(anilistTask, kitsuTask).ConfigureAwait(false);
        return [
            (await anilistTask.ConfigureAwait(false), "AniList"),
            (await kitsuTask.ConfigureAwait(false), "Kitsu")
        ];
    }

    private async Task<List<(ProviderTagResult Result, string ProviderName)>> FetchMangaProviderResultsAsync(
        MediaTagLookupContext context,
        CancellationToken cancellationToken)
    {
        var mangaUpdatesTask = _mangaUpdates.GetTagsForTitleAsync(context, cancellationToken);
        var kitsuTask = _kitsu.GetTagsForTitleAsync(context, cancellationToken);
        await Task.WhenAll(mangaUpdatesTask, kitsuTask).ConfigureAwait(false);
        return [
            (await mangaUpdatesTask.ConfigureAwait(false), "MangaUpdates"),
            (await kitsuTask.ConfigureAwait(false), "Kitsu")
        ];
    }

    private async Task<List<(ProviderTagResult Result, string ProviderName)>> GetNovelTagsAsync(
        MediaTagLookupContext context,
        CancellationToken cancellationToken)
    {
        var mangaUpdatesTask = _mangaUpdates.GetTagsForTitleAsync(context, cancellationToken);
        var kitsuTask = _kitsu.GetTagsForTitleAsync(context, cancellationToken);
        var novelFullTask = _novelFull.GetTagsForTitleAsync(context, cancellationToken);
        var catalogTask = _catalog.GetTagsForTitleAsync(context, cancellationToken);
        await Task.WhenAll(mangaUpdatesTask, kitsuTask, novelFullTask, catalogTask).ConfigureAwait(false);
        return [
            (await mangaUpdatesTask.ConfigureAwait(false), "MangaUpdates"),
            (await kitsuTask.ConfigureAwait(false), "Kitsu"),
            (await novelFullTask.ConfigureAwait(false), "NovelFull"),
            (await catalogTask.ConfigureAwait(false), "Catalog")
        ];
    }

    private static MediaTagLookupContext BuildLookupContext(
        BookmarkTagDomain domain,
        string canonicalTitle,
        string? url,
        string? folderPath,
        bool bypassProviderCache)
    {
        var normalizedTitle = MediaTitleNormalizer.Normalize(canonicalTitle, url, domain);
        return new MediaTagLookupContext(canonicalTitle, url, domain, folderPath, normalizedTitle, bypassProviderCache);
    }
}
