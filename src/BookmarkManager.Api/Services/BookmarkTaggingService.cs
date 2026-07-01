using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services;

public sealed class BookmarkTaggingService
{
    private readonly IAnilistTagProvider _anilist;
    private readonly IMangaUpdatesTagProvider _mangaUpdates;
    private readonly TagExtractorService _localTagExtractor;
    private readonly ILogger<BookmarkTaggingService> _logger;

    public BookmarkTaggingService(
        IAnilistTagProvider anilist,
        IMangaUpdatesTagProvider mangaUpdates,
        TagExtractorService localTagExtractor,
        ILogger<BookmarkTaggingService> logger)
    {
        _anilist = anilist;
        _mangaUpdates = mangaUpdates;
        _localTagExtractor = localTagExtractor;
        _logger = logger;
    }

    public async Task<List<string>> GetTagsAsync(
        string title,
        string? url,
        string? folderPath,
        BookmarkTagDomainDto requestedDomain,
        CancellationToken cancellationToken)
    {
        var classification = BookmarkTagClassifier.Classify(title, url, folderPath, requestedDomain);
        _logger.LogDebug(
            "Bookmark '{Title}' classified as {Domain}: {Reason}",
            title,
            classification.Domain,
            classification.Reason);

        List<string> tags = [];
        if (classification.ShouldUseAniList)
        {
            tags = await _anilist.GetTagsForTitleAsync(title, url, classification.Domain, cancellationToken).ConfigureAwait(false);
        }
        else if (classification.ShouldUseMangaUpdates)
        {
            tags = await _mangaUpdates.GetTagsForTitleAsync(title, url, classification.Domain, cancellationToken).ConfigureAwait(false);
        }

        if (tags.Count == 0)
            tags = _localTagExtractor.ExtractTags(title, url, classification.Domain).ToList();

        return tags;
    }

    public async Task<Dictionary<Guid, List<string>>> GetTagsForBatchAsync(
        IReadOnlyCollection<BookmarkTagCandidateDto> items,
        string? folderPath,
        BookmarkTagDomainDto requestedDomain,
        CancellationToken cancellationToken)
    {
        var results = new Dictionary<Guid, List<string>>();
        var lookupCache = new Dictionary<(BookmarkTagDomain Domain, string CleanTitle), List<string>>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classification = BookmarkTagClassifier.Classify(item.Title, item.Url, folderPath, requestedDomain);
            var key = (classification.Domain, classification.CleanTitle);

            if (!lookupCache.TryGetValue(key, out var tags))
            {
                try
                {
                    tags = await GetTagsAsync(item.Title, item.Url, folderPath, requestedDomain, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Per-item tag lookup failed for '{Title}', falling back to local extractor.", item.Title);
                    tags = [];
                }
                lookupCache[key] = tags;
            }

            // If the provider returned nothing, try the local extractor so the
            // bookmark is never left empty just because an external API hiccupped.
            if (tags.Count == 0)
                tags = _localTagExtractor.ExtractTags(item.Title, item.Url, classification.Domain).ToList();

            results[item.Id] = tags.ToList();
        }

        _logger.LogInformation(
            "Tagged batch of {Total} bookmarks with {Unique} unique provider/local lookups after dedupe.",
            items.Count,
            lookupCache.Count);

        return results;
    }
}
