using System.Collections.Concurrent;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.BookmarkTagging;

internal sealed partial class AiBookmarkAutoTaggingService
{
    private async Task PrefetchSourceTagsAsync(
        IReadOnlyList<SourceTagLookupRequest> requests,
        ConcurrentDictionary<SourceTagLookupKey, List<string>> cache,
        ConcurrentDictionary<SourceTagLookupKey, byte> providerFailedKeys,
        AiAutoTagSummaryDto summary,
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
        ConcurrentDictionary<SourceTagLookupKey, List<string>> cache,
        ConcurrentDictionary<SourceTagLookupKey, byte> providerFailedKeys,
        AiAutoTagSummaryDto summary,
        SemaphoreSlim semaphore,
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

    private async Task<List<string>> FetchSourceTagsAsync(
        BookmarkTagDomain domain,
        string canonicalTitle,
        string? url,
        string? folderPath,
        CancellationToken cancellationToken)
    {
        var context = BuildLookupContext(domain, canonicalTitle, url, folderPath);
        List<ProviderTagResult> results = domain switch
        {
            BookmarkTagDomain.Anime => await FetchAnimeProviderResultsAsync(context, cancellationToken).ConfigureAwait(false),
            BookmarkTagDomain.Manga => await FetchMangaProviderResultsAsync(context, cancellationToken).ConfigureAwait(false),
            BookmarkTagDomain.Novel => await GetNovelTagsAsync(context, cancellationToken).ConfigureAwait(false),
            _ => []
        };

        return results
            .Where(result => !result.WasRejected)
            .SelectMany(result => result.Tags)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<ProviderTagResult>> FetchAnimeProviderResultsAsync(
        MediaTagLookupContext context,
        CancellationToken cancellationToken)
    {
        var anilistTask = _anilist.GetTagsForTitleAsync(context, cancellationToken);
        var kitsuTask = _kitsu.GetTagsForTitleAsync(context, cancellationToken);
        await Task.WhenAll(anilistTask, kitsuTask).ConfigureAwait(false);
        return [await anilistTask.ConfigureAwait(false), await kitsuTask.ConfigureAwait(false)];
    }

    private async Task<List<ProviderTagResult>> FetchMangaProviderResultsAsync(
        MediaTagLookupContext context,
        CancellationToken cancellationToken)
    {
        var mangaUpdatesTask = _mangaUpdates.GetTagsForTitleAsync(context, cancellationToken);
        var kitsuTask = _kitsu.GetTagsForTitleAsync(context, cancellationToken);
        await Task.WhenAll(mangaUpdatesTask, kitsuTask).ConfigureAwait(false);
        return [await mangaUpdatesTask.ConfigureAwait(false), await kitsuTask.ConfigureAwait(false)];
    }

    private async Task<List<ProviderTagResult>> GetNovelTagsAsync(
        MediaTagLookupContext context,
        CancellationToken cancellationToken)
    {
        var novelFull = await _novelFull.GetTagsForTitleAsync(context, cancellationToken).ConfigureAwait(false);
        return [novelFull];
    }

    private static MediaTagLookupContext BuildLookupContext(
        BookmarkTagDomain domain,
        string canonicalTitle,
        string? url,
        string? folderPath)
    {
        var normalizedTitle = MediaTitleNormalizer.Normalize(canonicalTitle, url, domain);
        return new MediaTagLookupContext(canonicalTitle, url, domain, folderPath, normalizedTitle);
    }
}
