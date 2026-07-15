using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services;

public sealed class BookmarkTaggingService
{
    private readonly IAnilistTagProvider _anilist;
    private readonly IMangaUpdatesTagProvider _mangaUpdates;
    private readonly IKitsuTagProvider _kitsu;
    private readonly INovelFullTagProvider _novelFull;
    private readonly ICatalogTagProvider _catalog;
    private readonly TagExtractorService _localTagExtractor;
    private readonly ILogger<BookmarkTaggingService> _logger;

    public BookmarkTaggingService(
        IAnilistTagProvider anilist,
        IMangaUpdatesTagProvider mangaUpdates,
        IKitsuTagProvider kitsu,
        INovelFullTagProvider novelFull,
        ICatalogTagProvider catalog,
        TagExtractorService localTagExtractor,
        ILogger<BookmarkTaggingService> logger)
    {
        _anilist = anilist;
        _mangaUpdates = mangaUpdates;
        _kitsu = kitsu;
        _novelFull = novelFull;
        _catalog = catalog;
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
        var normalizedTitle = MediaTitleNormalizer.Normalize(title, url, classification.Domain);
        var lookupContext = new MediaTagLookupContext(title, url, classification.Domain, folderPath, normalizedTitle);
        _logger.LogDebug(
            "Bookmark '{Title}' classified as {Domain}: {Reason}. Normalized candidates: {Candidates}",
            title,
            classification.Domain,
            classification.Reason,
            string.Join(" | ", normalizedTitle.Candidates.Select(candidate => candidate.Query)));

        var (provider, tags, wasRejected, rejectionReason) = await QueryProvidersAsync(
            title,
            url,
            folderPath,
            classification,
            lookupContext,
            requestedDomain,
            cancellationToken).ConfigureAwait(false);

        var source = provider;
        var confidence = tags.Count == 0 ? BookmarkTagConfidence.None : BookmarkTagConfidence.High;
        var state = provider == BookmarkTagSource.None ? BookmarkTagResultState.ProviderNotApplicable : BookmarkTagResultState.ProviderSuccess;
        var reason = tags.Count == 0 ? "Provider returned no tags." : "Provider returned tags.";

        if (wasRejected)
        {
            tags = classification.Domain switch
            {
                BookmarkTagDomain.Anime => ["Anime"],
                BookmarkTagDomain.Manga => ["Manga"],
                BookmarkTagDomain.Novel => ["Novel"],
                _ => []
            };
            source = tags.Count > 0 ? BookmarkTagSource.LocalHeuristic : BookmarkTagSource.None;
            confidence = tags.Count > 0 ? BookmarkTagConfidence.Low : BookmarkTagConfidence.None;
            state = BookmarkTagResultState.ProviderNoMatch;
            reason = $"Provider rejected query. {rejectionReason}";
        }
        else if (tags.Count == 0)
        {
            tags = _localTagExtractor.ExtractTags(title, url, classification.Domain).ToList();
            source = tags.Count == 0 ? BookmarkTagSource.None : BookmarkTagSource.LocalHeuristic;
            confidence = tags.Count == 0 ? BookmarkTagConfidence.None : BookmarkTagConfidence.Low;
            state = tags.Count == 0 ? BookmarkTagResultState.ProviderNoMatch : BookmarkTagResultState.Fallback;
            reason = tags.Count == 0 ? "No provider or local tags found." : "Provider returned no tags; used low-confidence local fallback.";
        }

        LogTagDecision(title, url, folderPath, requestedDomain, classification, provider, source, confidence, state, tags, reason);

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
            var normalizedTitle = MediaTitleNormalizer.Normalize(item.Title, item.Url, classification.Domain);
            var key = (classification.Domain, normalizedTitle.Candidates.FirstOrDefault()?.Query ?? classification.CleanTitle);

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

            results[item.Id] = tags.ToList();
        }

        _logger.LogInformation(
            "Tagged batch of {Total} bookmarks with {Unique} unique provider/local lookups after dedupe.",
            items.Count,
            lookupCache.Count);

        return results;
    }

    private async Task<(BookmarkTagSource Source, List<string> Tags, bool WasRejected, string? RejectionReason)> QueryProvidersAsync(
        string title,
        string? url,
        string? folderPath,
        BookmarkTagClassification classification,
        MediaTagLookupContext lookupContext,
        BookmarkTagDomainDto requestedDomain,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task<(BookmarkTagSource Source, ProviderTagResult Result)>>();
        if (classification.ShouldUseAniList)
        {
            tasks.Add(Task.Run(async () => (BookmarkTagSource.AniList, await _anilist.GetTagsForTitleAsync(lookupContext, cancellationToken))));
            tasks.Add(Task.Run(async () => (BookmarkTagSource.Kitsu, await _kitsu.GetTagsForTitleAsync(lookupContext, cancellationToken))));
        }
        else if (classification.ShouldUseMangaUpdates)
        {
            if (classification.Domain == BookmarkTagDomain.Novel)
            {
                tasks.Add(Task.Run(async () => (BookmarkTagSource.MangaUpdates, await _mangaUpdates.GetTagsForTitleAsync(lookupContext, cancellationToken))));
                tasks.Add(Task.Run(async () => (BookmarkTagSource.Kitsu, await _kitsu.GetTagsForTitleAsync(lookupContext, cancellationToken))));
                tasks.Add(Task.Run(async () => (BookmarkTagSource.NovelFull, await _novelFull.GetTagsForTitleAsync(lookupContext, cancellationToken))));
                tasks.Add(Task.Run(async () => (BookmarkTagSource.Catalog, await _catalog.GetTagsForTitleAsync(lookupContext, cancellationToken))));
            }
            else
            {
                tasks.Add(Task.Run(async () => (BookmarkTagSource.MangaUpdates, await _mangaUpdates.GetTagsForTitleAsync(lookupContext, cancellationToken))));
                tasks.Add(Task.Run(async () => (BookmarkTagSource.Kitsu, await _kitsu.GetTagsForTitleAsync(lookupContext, cancellationToken))));
            }
        }
        else if (classification.Domain == BookmarkTagDomain.General && classification.IsEligibleForDualProviderLookup && requestedDomain == BookmarkTagDomainDto.Auto)
        {
            var animeContext = lookupContext with { Domain = BookmarkTagDomain.Anime, NormalizedTitle = MediaTitleNormalizer.Normalize(title, url, BookmarkTagDomain.Anime) };
            var mangaContext = lookupContext with { Domain = BookmarkTagDomain.Manga, NormalizedTitle = MediaTitleNormalizer.Normalize(title, url, BookmarkTagDomain.Manga) };
            var novelContext = lookupContext with { Domain = BookmarkTagDomain.Novel, NormalizedTitle = MediaTitleNormalizer.Normalize(title, url, BookmarkTagDomain.Novel) };

            tasks.Add(Task.Run(async () => (BookmarkTagSource.AniList, await _anilist.GetTagsForTitleAsync(animeContext, cancellationToken))));
            tasks.Add(Task.Run(async () => (BookmarkTagSource.Kitsu, await _kitsu.GetTagsForTitleAsync(animeContext, cancellationToken))));

            tasks.Add(Task.Run(async () => (BookmarkTagSource.MangaUpdates, await _mangaUpdates.GetTagsForTitleAsync(mangaContext, cancellationToken))));
            tasks.Add(Task.Run(async () => (BookmarkTagSource.Kitsu, await _kitsu.GetTagsForTitleAsync(mangaContext, cancellationToken))));

            tasks.Add(Task.Run(async () => (BookmarkTagSource.MangaUpdates, await _mangaUpdates.GetTagsForTitleAsync(novelContext, cancellationToken))));
            tasks.Add(Task.Run(async () => (BookmarkTagSource.Kitsu, await _kitsu.GetTagsForTitleAsync(novelContext, cancellationToken))));
            tasks.Add(Task.Run(async () => (BookmarkTagSource.NovelFull, await _novelFull.GetTagsForTitleAsync(novelContext, cancellationToken))));
            tasks.Add(Task.Run(async () => (BookmarkTagSource.Catalog, await _catalog.GetTagsForTitleAsync(novelContext, cancellationToken))));
        }

        if (tasks.Count == 0)
        {
            return (BookmarkTagSource.None, [], false, null);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var validResults = new List<(BookmarkTagSource Source, ProviderTagResult Result)>();
        foreach (var task in tasks)
        {
            var res = await task;
            if (!res.Result.WasRejected && res.Result.Tags.Count > 0)
            {
                validResults.Add(res);
            }
        }

        if (validResults.Count == 0)
        {
            var rejected = tasks.Select(t => t.Result).FirstOrDefault(r => r.Result != null && r.Result.WasRejected);
            if (rejected.Result != null)
            {
                return (rejected.Source, [], true, rejected.Result.RejectionReason);
            }
            return (BookmarkTagSource.None, [], false, null);
        }

        var sortedResults = validResults.OrderBy(r =>
        {
            bool matchesDomain = false;
            if (classification.Domain == BookmarkTagDomain.Novel && r.Result.Tags.Contains("Novel")) matchesDomain = true;
            if (classification.Domain == BookmarkTagDomain.Manga && (r.Result.Tags.Contains("Manga") || r.Result.Tags.Contains("Manhwa") || r.Result.Tags.Contains("Manhua"))) matchesDomain = true;
            if (classification.Domain == BookmarkTagDomain.Anime && r.Result.Tags.Contains("Anime")) matchesDomain = true;

            int domainScore = matchesDomain ? 0 : 1;
            int sourceScore = r.Source switch
            {
                BookmarkTagSource.MangaUpdates => 0,
                BookmarkTagSource.AniList => 0,
                BookmarkTagSource.Kitsu => 1,
                BookmarkTagSource.Catalog => 1,
                BookmarkTagSource.NovelFull => 2,
                _ => 3
            };
            return (domainScore, sourceScore);
        }).ToList();

        var primarySource = sortedResults.First().Source;
        var combinedTags = new List<string>();
        var uniqueTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var res in sortedResults)
        {
            foreach (var tag in res.Result.Tags)
            {
                if (uniqueTags.Add(tag))
                {
                    combinedTags.Add(tag);
                }
            }
        }

        string? domainTag = classification.Domain switch
        {
            BookmarkTagDomain.Anime => "Anime",
            BookmarkTagDomain.Manga => "Manga",
            BookmarkTagDomain.Novel => "Novel",
            _ => null
        };

        if (domainTag != null)
        {
            combinedTags.Remove(domainTag);
            combinedTags.Insert(0, domainTag);

            if (domainTag == "Novel")
            {
                combinedTags.Remove("Anime");
                combinedTags.Remove("Manga");
            }
            else if (domainTag == "Manga")
            {
                combinedTags.Remove("Anime");
                combinedTags.Remove("Novel");
            }
            else if (domainTag == "Anime")
            {
                combinedTags.Remove("Manga");
                combinedTags.Remove("Novel");
            }
        }

        return (primarySource, combinedTags, false, null);
    }

    private void LogTagDecision(
        string title,
        string? url,
        string? folderPath,
        BookmarkTagDomainDto requestedDomain,
        BookmarkTagClassification classification,
        BookmarkTagSource provider,
        BookmarkTagSource source,
        BookmarkTagConfidence confidence,
        BookmarkTagResultState state,
        IReadOnlyCollection<string> finalTags,
        string reason)
    {
        string? host = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            host = uri.Host;

        _logger.LogInformation(
            "Auto-tag decision: Title='{Title}', UrlHost='{UrlHost}', Folder='{FolderPath}', Requested={RequestedDomain}, Classified={ClassifiedDomain}, Provider={Provider}, Result={ResultState}, Source={Source}, Confidence={Confidence}, FinalTags=[{FinalTags}], Reason='{Reason}'",
            title,
            host,
            folderPath,
            requestedDomain,
            classification.Domain,
            provider,
            state,
            source,
            confidence,
            string.Join(", ", finalTags),
            reason);
    }
}
