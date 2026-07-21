using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services;

/// <summary>Auto-tag result plus the matched provider's cover art, when one was found.</summary>
public sealed record AutoTagOutcome(List<string> Tags, string? CoverImageUrl);

public sealed class BookmarkTaggingService
{
    private readonly IAnilistTagProvider _anilist;
    private readonly IMangaUpdatesTagProvider _mangaUpdates;
    private readonly IKitsuTagProvider _kitsu;
    private readonly ICatalogTagProvider _catalog;
    private readonly TagExtractorService _localTagExtractor;
    private readonly ILogger<BookmarkTaggingService> _logger;

    public sealed record BatchTagLookupResult(
        Dictionary<Guid, List<string>> Tags,
        Dictionary<Guid, string?> SuggestedTitles,
        Dictionary<Guid, List<TagScoreDto>> TagScores);

    private sealed record TagLookupResult(
        List<string> Tags,
        string? CanonicalTitle,
        string? CoverImageUrl,
        List<TagScoreDto> TagScores)
    {
        public TagLookupResult(List<string> tags, string? canonicalTitle)
            : this(tags, canonicalTitle, null, [])
        {
        }
    }

    /// <summary>Maps a provider source to the similarity threshold used to judge its match score.</summary>
    private static double GetSimilarityThreshold(BookmarkTagSource source) => source switch
    {
        BookmarkTagSource.AniList => SimilarityThresholds.AniList,
        BookmarkTagSource.Kitsu => SimilarityThresholds.Kitsu,
        BookmarkTagSource.MangaUpdates => SimilarityThresholds.MangaUpdates,
        BookmarkTagSource.Catalog => SimilarityThresholds.Catalog,
        _ => SimilarityThresholds.Default
    };

    public BookmarkTaggingService(
        IAnilistTagProvider anilist,
        IMangaUpdatesTagProvider mangaUpdates,
        IKitsuTagProvider kitsu,
        ICatalogTagProvider catalog,
        TagExtractorService localTagExtractor,
        ILogger<BookmarkTaggingService> logger)
    {
        _anilist = anilist;
        _mangaUpdates = mangaUpdates;
        _kitsu = kitsu;
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
        var result = await GetTagsWithCanonicalAsync(title, url, folderPath, requestedDomain, cancellationToken)
            .ConfigureAwait(false);
        return result.Tags;
    }

    /// <summary>
    /// Same as <see cref="GetTagsAsync"/> but also surfaces the matched provider's
    /// cover art (currently AniList) so callers can persist a real poster instead of
    /// relying on a page-scraped og:image.
    /// </summary>
    public async Task<AutoTagOutcome> GetTagsWithCoverAsync(
        string title,
        string? url,
        string? folderPath,
        BookmarkTagDomainDto requestedDomain,
        CancellationToken cancellationToken)
    {
        var result = await GetTagsWithCanonicalAsync(title, url, folderPath, requestedDomain, cancellationToken)
            .ConfigureAwait(false);
        return new AutoTagOutcome(result.Tags, result.CoverImageUrl);
    }

    public async Task<BatchTagLookupResult> GetTagsForBatchAsync(
        IReadOnlyCollection<BookmarkTagCandidateDto> items,
        string? folderPath,
        BookmarkTagDomainDto requestedDomain,
        CancellationToken cancellationToken)
    {
        var tagsById = new Dictionary<Guid, List<string>>();
        var suggestedById = new Dictionary<Guid, string?>();
        var tagScoresById = new Dictionary<Guid, List<TagScoreDto>>();
        var lookupCache = new Dictionary<(BookmarkTagDomain Domain, string CleanTitle), TagLookupResult>();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classification = BookmarkTagClassifier.Classify(item.Title, item.Url, folderPath, requestedDomain);
            var normalizedTitle = MediaTitleNormalizer.Normalize(item.Title, item.Url, classification.Domain);
            var key = (classification.Domain, normalizedTitle.Candidates.FirstOrDefault()?.Query ?? classification.CleanTitle);

            if (!lookupCache.TryGetValue(key, out var lookup))
            {
                try
                {
                    lookup = await GetTagsWithCanonicalAsync(item.Title, item.Url, folderPath, requestedDomain, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Per-item tag lookup failed for '{Title}', falling back to local extractor.", item.Title);
                    lookup = new TagLookupResult([], null);
                }
                lookupCache[key] = lookup;
            }

            tagsById[item.Id] = lookup.Tags.ToList();
            suggestedById[item.Id] = BookmarkTitleSuggestionBuilder.Build(
                lookup.CanonicalTitle,
                item.Title,
                item.Url);
            tagScoresById[item.Id] = lookup.TagScores;
        }

        _logger.LogInformation(
            "Tagged batch of {Total} bookmarks with {Unique} unique provider/local lookups after dedupe.",
            items.Count,
            lookupCache.Count);

        return new BatchTagLookupResult(tagsById, suggestedById, tagScoresById);
    }

    private async Task<TagLookupResult> GetTagsWithCanonicalAsync(
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

        var (provider, tags, wasRejected, rejectionReason, canonicalTitle, coverImageUrl, tagScores) = await QueryProvidersAsync(
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
            canonicalTitle = null;
            coverImageUrl = null;
            tagScores = [];
        }
        else if (tags.Count == 0)
        {
            tags = _localTagExtractor.ExtractTags(title, url, classification.Domain).ToList();
            source = tags.Count == 0 ? BookmarkTagSource.None : BookmarkTagSource.LocalHeuristic;
            confidence = tags.Count == 0 ? BookmarkTagConfidence.None : BookmarkTagConfidence.Low;
            state = tags.Count == 0 ? BookmarkTagResultState.ProviderNoMatch : BookmarkTagResultState.Fallback;
            reason = tags.Count == 0 ? "No provider or local tags found." : "Provider returned no tags; used low-confidence local fallback.";
            canonicalTitle = null;
            coverImageUrl = null;
            tagScores = [];
        }

        LogTagDecision(title, url, folderPath, requestedDomain, classification, provider, source, confidence, state, tags, reason);

        return new TagLookupResult(tags, canonicalTitle, coverImageUrl, tagScores);
    }

    private async Task<(BookmarkTagSource Source, List<string> Tags, bool WasRejected, string? RejectionReason, string? CanonicalTitle, string? CoverImageUrl, List<TagScoreDto> TagScores)> QueryProvidersAsync(
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
            tasks.Add(Task.Run(async () => (BookmarkTagSource.Catalog, await _catalog.GetTagsForTitleAsync(novelContext, cancellationToken))));
        }

        if (tasks.Count == 0)
        {
            return (BookmarkTagSource.None, [], false, null, null, null, []);
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
                return (rejected.Source, [], true, rejected.Result.RejectionReason, null, null, []);
            }
            return (BookmarkTagSource.None, [], false, null, null, null, []);
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
                _ => 3
            };
            return (domainScore, sourceScore);
        }).ToList();

        var primarySource = sortedResults.First().Source;
        var combinedTags = new List<string>();
        var uniqueTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tagScores = new List<TagScoreDto>();

        foreach (var res in sortedResults)
        {
            foreach (var tag in res.Result.Tags)
            {
                if (uniqueTags.Add(tag))
                {
                    combinedTags.Add(tag);

                    var threshold = GetSimilarityThreshold(res.Source);
                    var meetsThreshold = res.Result.MatchScore.HasValue && res.Result.MatchScore.Value >= threshold;
                    tagScores.Add(new TagScoreDto(tag, res.Source.ToString(), res.Result.MatchScore, meetsThreshold));
                }
            }
        }

        var canonicalTitle = SelectCanonicalTitle(sortedResults, lookupContext, classification);

        // Take the poster from the highest-priority result that carries one (AniList first).
        var coverImageUrl = sortedResults
            .Select(r => r.Result.CoverImageUrl)
            .FirstOrDefault(cover => !string.IsNullOrWhiteSpace(cover));

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

        return (primarySource, combinedTags, false, null, canonicalTitle, coverImageUrl, tagScores);
    }

    private const double MinCanonicalTitleSimilarity = 0.40;

    private static string? SelectCanonicalTitle(
        IReadOnlyList<(BookmarkTagSource Source, ProviderTagResult Result)> sortedResults,
        MediaTagLookupContext lookupContext,
        BookmarkTagClassification classification)
    {
        var referenceTitles = BuildCanonicalReferenceTitles(lookupContext, classification);
        foreach (var res in sortedResults)
        {
            var candidate = res.Result.CanonicalTitle?.Trim();
            if (string.IsNullOrEmpty(candidate))
                continue;

            if (IsAcceptableCanonicalTitle(candidate, referenceTitles))
                return candidate;
        }

        return null;
    }

    private static List<string> BuildCanonicalReferenceTitles(
        MediaTagLookupContext lookupContext,
        BookmarkTagClassification classification)
    {
        var references = new List<string>();
        if (!string.IsNullOrWhiteSpace(classification.CleanTitle))
            references.Add(classification.CleanTitle.Trim());

        foreach (var candidate in lookupContext.NormalizedTitle.Candidates.Take(MediaTitleNormalizer.MaxProviderCandidates))
        {
            if (!string.IsNullOrWhiteSpace(candidate.Query))
                references.Add(candidate.Query.Trim());
        }

        var wholeTitle = MediaTitleNormalizer.NormalizeForSearch(lookupContext.OriginalTitle);
        if (wholeTitle.Length >= 2)
            references.Add(wholeTitle);

        return references
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsAcceptableCanonicalTitle(string canonicalTitle, IReadOnlyList<string> referenceTitles)
    {
        if (referenceTitles.Count == 0)
            return true;

        var canonicalTokens = MediaTitleNormalizer.NormalizeForSearch(canonicalTitle)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bestReferenceWordCount = referenceTitles
            .Select(title => title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
            .DefaultIfEmpty(0)
            .Max();

        if (canonicalTokens.Length == 1 && bestReferenceWordCount >= 3)
            return false;

        foreach (var reference in referenceTitles)
        {
            if (MediaTitleNormalizer.ScoreTitleSimilarity(reference, [canonicalTitle]) >= MinCanonicalTitleSimilarity)
                return true;
        }

        return false;
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
