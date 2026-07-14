using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace BookmarkManager.Api.Services.BookmarkTagging;

internal sealed class AiBookmarkAutoTaggingService
{
    private const double MinimumConfidence = 0.70;
    private const int MaxTags = 15;
    private const int TagSaveBatchSize = 10;
    private const int ProviderLookupConcurrency = 6;

    private readonly AppDbContext _db;
    private readonly AiSeriesIdentifierService _identifier;
    private readonly IAnilistTagProvider _anilist;
    private readonly IMangaUpdatesTagProvider _mangaUpdates;
    private readonly IKitsuTagProvider _kitsu;
    private readonly INovelFullTagProvider _novelFull;
    private readonly ILogger<AiBookmarkAutoTaggingService> _logger;

    public AiBookmarkAutoTaggingService(
        AppDbContext db,
        AiSeriesIdentifierService identifier,
        IAnilistTagProvider anilist,
        IMangaUpdatesTagProvider mangaUpdates,
        IKitsuTagProvider kitsu,
        INovelFullTagProvider novelFull,
        ILogger<AiBookmarkAutoTaggingService> logger)
    {
        _db = db;
        _identifier = identifier;
        _anilist = anilist;
        _mangaUpdates = mangaUpdates;
        _kitsu = kitsu;
        _novelFull = novelFull;
        _logger = logger;
    }

    public async Task<AiAutoTagSummaryDto> TagFolderAsync(
        Guid folderId,
        bool forceRefresh,
        CancellationToken cancellationToken)
        => await TagFolderAsync(folderId, forceRefresh, maxCandidates: null, excludedBookmarkIds: [], cancellationToken).ConfigureAwait(false);

    public async Task<AiAutoTagSummaryDto> TagFolderAsync(
        Guid folderId,
        bool forceRefresh,
        int? maxCandidates,
        IReadOnlyCollection<Guid> excludedBookmarkIds,
        CancellationToken cancellationToken)
    {
        using var telemetryScope = AutoTagRunTelemetry.BeginScope();
        var summary = new AiAutoTagSummaryDto();
        var runState = new TagFolderRunState();
        var runCanceled = false;

        try
        {
            return await TagFolderCoreAsync(
                folderId,
                forceRefresh,
                maxCandidates,
                excludedBookmarkIds,
                summary,
                runState,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            runCanceled = true;
        }

        if (runState.TagsPendingSave > 0)
        {
            await SaveTaggedBookmarksAsync(summary, runState.TagsPendingSave, CancellationToken.None).ConfigureAwait(false);
            runState.TagsPendingSave = 0;
        }

        summary.RemainingCandidates = Math.Max(0, runState.TotalEligibleCount - summary.ProcessedBookmarkIds.Count);
        summary.HasMore = summary.RemainingCandidates > 0;
        summary.Messages.Add(runCanceled
            ? "Run canceled; saved tagged bookmarks completed before cancel."
            : "Run ended before completion.");
        telemetryScope.AppendSummaryTo(summary.Messages);
        return summary;
    }

    private async Task<AiAutoTagSummaryDto> TagFolderCoreAsync(
        Guid folderId,
        bool forceRefresh,
        int? maxCandidates,
        IReadOnlyCollection<Guid> excludedBookmarkIds,
        AiAutoTagSummaryDto summary,
        TagFolderRunState runState,
        CancellationToken cancellationToken)
    {
        var excluded = excludedBookmarkIds.ToHashSet();
        var allNodes = await _db.BookmarkNodes
            .Where(node => !node.IsDeleted)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var selectedFolder = allNodes.SingleOrDefault(node => node.Id == folderId && node.Type == NodeType.Folder);
        if (selectedFolder is null)
        {
            summary.Messages.Add($"Folder {folderId} was not found.");
            return summary;
        }

        var folderIds = GetDescendantFolderIds(allNodes, folderId);
        var folderPaths = BuildFolderPaths(allNodes.Where(node => node.Type == NodeType.Folder));
        var bookmarks = allNodes
            .Where(node => node.Type == NodeType.Bookmark && node.ParentId.HasValue && folderIds.Contains(node.ParentId.Value))
            .OrderBy(node => folderPaths.GetValueOrDefault(node.ParentId ?? Guid.Empty, string.Empty), StringComparer.OrdinalIgnoreCase)
            .ThenBy(node => node.Position)
            .ThenBy(node => node.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        summary.TotalCandidates = bookmarks.Count;
        summary.Messages.Add($"Found {bookmarks.Count} bookmark(s) in '{selectedFolder.Title}'.");

        var candidates = new List<BookmarkCandidate>();
        foreach (var bookmark in bookmarks)
        {
            if (excluded.Contains(bookmark.Id))
                continue;

            if (!forceRefresh && !string.IsNullOrWhiteSpace(bookmark.Tags))
            {
                summary.SkippedAlreadyTagged++;
                summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                {
                    BookmarkId = bookmark.Id,
                    Title = bookmark.Title,
                    Status = "AlreadyTagged"
                });
                continue;
            }

            candidates.Add(new BookmarkCandidate(
                bookmark,
                folderPaths.GetValueOrDefault(bookmark.ParentId ?? Guid.Empty, string.Empty)));
        }

        var totalEligibleCountLocal = candidates.Count;
        runState.TotalEligibleCount = totalEligibleCountLocal;

        if (maxCandidates is > 0 && candidates.Count > maxCandidates.Value)
        {
            candidates = candidates.Take(maxCandidates.Value).ToList();
            summary.Messages.Add($"Processing next {candidates.Count} bookmark(s); more remain after this batch.");
        }

        if (candidates.Count == 0)
        {
            summary.Messages.Add("All bookmarks already have tags. Nothing to process.");
            summary.RemainingCandidates = 0;
            summary.HasMore = false;
            return summary;
        }

        var deterministicCandidates = new List<BookmarkCandidate>();
        var ambiguousCandidates = new List<BookmarkCandidate>();

        foreach (var candidate in candidates)
        {
            var classification = BookmarkMediaCandidateClassifier.Classify(
                candidate.Bookmark.Title,
                candidate.Bookmark.Url,
                candidate.FolderPath);

            if (!classification.RequiresAi)
            {
                deterministicCandidates.Add(candidate);
            }
            else
            {
                ambiguousCandidates.Add(candidate);
            }
        }

        var sourceTagCache = new ConcurrentDictionary<SourceTagLookupKey, List<string>>();
        var providerFailedKeys = new ConcurrentDictionary<SourceTagLookupKey, byte>();

        // 1. Process deterministic candidates (bypass AI)
        if (deterministicCandidates.Count > 0)
        {
            summary.Messages.Add($"Deterministic pass: processing {deterministicCandidates.Count} obvious candidate(s) without AI.");

            var deterministicLookups = new List<SourceTagLookupRequest>();
            foreach (var candidate in deterministicCandidates)
            {
                var classification = BookmarkMediaCandidateClassifier.Classify(
                    candidate.Bookmark.Title,
                    candidate.Bookmark.Url,
                    candidate.FolderPath);
                var route = ResolveDeterministicRoute(candidate.Bookmark, candidate.FolderPath, classification.Domain);
                deterministicLookups.Add(new SourceTagLookupRequest(
                    new SourceTagLookupKey(route.Domain, NormalizeCacheTitle(classification.CanonicalTitle)),
                    classification.CanonicalTitle,
                    candidate.Bookmark.Url,
                    candidate.FolderPath));
            }

            await PrefetchSourceTagsAsync(
                    deterministicLookups,
                    sourceTagCache,
                    providerFailedKeys,
                    summary,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var candidate in deterministicCandidates)
            {
                var classification = BookmarkMediaCandidateClassifier.Classify(
                    candidate.Bookmark.Title,
                    candidate.Bookmark.Url,
                    candidate.FolderPath);

                var route = ResolveDeterministicRoute(candidate.Bookmark, candidate.FolderPath, classification.Domain);
                var cacheKey = new SourceTagLookupKey(route.Domain, NormalizeCacheTitle(classification.CanonicalTitle));
                sourceTagCache.TryGetValue(cacheKey, out var sourceTags);
                sourceTags ??= [];
                var providerFailed = providerFailedKeys.ContainsKey(cacheKey);

                if (providerFailed)
                {
                    summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                    {
                        BookmarkId = candidate.Bookmark.Id,
                        Title = candidate.Bookmark.Title,
                        Status = "ProviderFailed",
                        Reason = "Provider lookup threw an exception."
                    });
                    summary.ProcessedBookmarkIds.Add(candidate.Bookmark.Id);
                    continue;
                }

                if (!sourceTags.Any(tag => !string.IsNullOrWhiteSpace(tag)))
                {
                    summary.SkippedNoSourceTags++;
                    summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                    {
                        BookmarkId = candidate.Bookmark.Id,
                        Title = candidate.Bookmark.Title,
                        Status = "NoSourceTags",
                        Reason = "No source tags found on providers."
                    });
                    summary.ProcessedBookmarkIds.Add(candidate.Bookmark.Id);
                    summary.Messages.Add($"  ✗ '{classification.CanonicalTitle}' — no source tags found");
                    continue;
                }

                var finalTags = MergeTags(route.DomainTag!, sourceTags);
                if (finalTags.Count == 0)
                {
                    summary.SkippedNoSourceTags++;
                    summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                    {
                        BookmarkId = candidate.Bookmark.Id,
                        Title = candidate.Bookmark.Title,
                        Status = "NoSourceTags",
                        Reason = "Merged tags count was zero."
                    });
                    summary.ProcessedBookmarkIds.Add(candidate.Bookmark.Id);
                    continue;
                }

                candidate.Bookmark.Tags = string.Join(',', finalTags);
                candidate.Bookmark.UpdatedAt = DateTime.UtcNow;
                summary.Tagged++;
                runState.TagsPendingSave++;
                summary.ProcessedBookmarkIds.Add(candidate.Bookmark.Id);
                summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                {
                    BookmarkId = candidate.Bookmark.Id,
                    Title = candidate.Bookmark.Title,
                    Status = "DeterministicClassified",
                    Reason = $"Classified deterministically as {classification.Domain}."
                });
                summary.Messages.Add($"  ✓ '{classification.CanonicalTitle}' tagged: [{string.Join(", ", finalTags)}]");
                runState.TagsPendingSave = await SaveTaggedBookmarksIfNeededAsync(summary, runState.TagsPendingSave, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // 2. Process ambiguous candidates via AI
        if (ambiguousCandidates.Count > 0)
        {
            summary.Messages.Add($"AI pass: sending {ambiguousCandidates.Count} ambiguous candidate(s) to AI for series identification.");
            var identificationSummary = await _identifier.IdentifyAsync(
                    ambiguousCandidates.Select(candidate => new AiSeriesIdentifyCandidate(
                        candidate.Bookmark.Id,
                        candidate.Bookmark.Title,
                        candidate.Bookmark.Url,
                        candidate.FolderPath)),
                    cancellationToken)
                .ConfigureAwait(false);

            summary.FailedChunks += identificationSummary.FailedChunks;
            summary.Messages.AddRange(identificationSummary.Messages);

            if (identificationSummary.IsRateLimited)
            {
                summary.StopForRateLimit = true;
                summary.RateLimited += ambiguousCandidates.Count - identificationSummary.Items.Count;
                summary.PendingRetry += ambiguousCandidates.Count - identificationSummary.Items.Count;
                summary.RetryAfterSeconds = identificationSummary.RetryAfter.HasValue 
                    ? (int)identificationSummary.RetryAfter.Value.TotalSeconds 
                    : 60;
            }
            else if (identificationSummary.FailedChunks > 0)
            {
                var identifiedIds = identificationSummary.Items.Select(item => item.Id).ToHashSet();
                var failedCount = ambiguousCandidates.Count(ac => !identifiedIds.Contains(ac.Bookmark.Id));
                summary.PendingRetry += failedCount;
            }

            var byBookmarkId = ambiguousCandidates.ToDictionary(candidate => candidate.Bookmark.Id);
            var identifiedBookmarkIds = identificationSummary.Items.Select(item => item.Id).ToHashSet();

            foreach (var ac in ambiguousCandidates)
            {
                if (!identifiedBookmarkIds.Contains(ac.Bookmark.Id))
                {
                    if (identificationSummary.IsRateLimited)
                    {
                        summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                        {
                            BookmarkId = ac.Bookmark.Id,
                            Title = ac.Bookmark.Title,
                            Status = "RateLimited",
                            Reason = "Rate limit hit on AI provider."
                        });
                    }
                    else if (identificationSummary.FailedChunks > 0)
                    {
                        summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                        {
                            BookmarkId = ac.Bookmark.Id,
                            Title = ac.Bookmark.Title,
                            Status = "AiInvalidResponse",
                            Reason = "AI returned an invalid response structure."
                        });
                    }
                    else
                    {
                        summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                        {
                            BookmarkId = ac.Bookmark.Id,
                            Title = ac.Bookmark.Title,
                            Status = "AiPendingRetry",
                            Reason = "AI identification did not succeed for this item."
                        });
                    }
                }
            }

            var aiLookups = new List<SourceTagLookupRequest>();
            var aiApplyItems = new List<(AiSeriesIdentification Identification, BookmarkCandidate Candidate, RouteDecision Route)>();

            foreach (var identification in identificationSummary.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!byBookmarkId.TryGetValue(identification.Id, out var candidate))
                    continue;

                if (identification.Confidence < MinimumConfidence)
                {
                    summary.SkippedLowConfidence++;
                    summary.ProcessedBookmarkIds.Add(candidate.Bookmark.Id);
                    summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                    {
                        BookmarkId = candidate.Bookmark.Id,
                        Title = candidate.Bookmark.Title,
                        Status = "LowConfidence",
                        Reason = $"AI confidence was low ({identification.Confidence:0.00})."
                    });
                    summary.Messages.Add($"  ✗ '{identification.CanonicalTitle}' — low confidence ({identification.Confidence:0.00})");
                    continue;
                }

                var route = ResolveRoute(candidate.Bookmark, candidate.FolderPath, identification);
                if (route.Domain == BookmarkTagDomain.General || route.DomainTag is null)
                {
                    summary.SkippedNoSourceTags++;
                    summary.ProcessedBookmarkIds.Add(candidate.Bookmark.Id);
                    summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                    {
                        BookmarkId = candidate.Bookmark.Id,
                        Title = candidate.Bookmark.Title,
                        Status = "NoSourceTags",
                        Reason = "No matching media source (general)."
                    });
                    summary.Messages.Add($"  ✗ '{identification.CanonicalTitle}' — no matching media source (general)");
                    continue;
                }

                aiLookups.Add(new SourceTagLookupRequest(
                    new SourceTagLookupKey(route.Domain, NormalizeCacheTitle(identification.CanonicalTitle)),
                    identification.CanonicalTitle,
                    candidate.Bookmark.Url,
                    candidate.FolderPath));
                aiApplyItems.Add((identification, candidate, route));
            }

            if (aiLookups.Count > 0)
            {
                await PrefetchSourceTagsAsync(
                        aiLookups,
                        sourceTagCache,
                        providerFailedKeys,
                        summary,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var (identification, candidate, route) in aiApplyItems)
            {
                var cacheKey = new SourceTagLookupKey(route.Domain, NormalizeCacheTitle(identification.CanonicalTitle));
                sourceTagCache.TryGetValue(cacheKey, out var sourceTags);
                sourceTags ??= [];
                var providerFailed = providerFailedKeys.ContainsKey(cacheKey);

                if (providerFailed)
                {
                    summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                    {
                        BookmarkId = candidate.Bookmark.Id,
                        Title = candidate.Bookmark.Title,
                        Status = "ProviderFailed",
                        Reason = "Provider lookup threw an exception."
                    });
                    summary.ProcessedBookmarkIds.Add(candidate.Bookmark.Id);
                    continue;
                }

                if (!sourceTags.Any(tag => !string.IsNullOrWhiteSpace(tag)))
                {
                    summary.SkippedNoSourceTags++;
                    summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                    {
                        BookmarkId = candidate.Bookmark.Id,
                        Title = candidate.Bookmark.Title,
                        Status = "NoSourceTags",
                        Reason = "No source tags found on providers."
                    });
                    summary.ProcessedBookmarkIds.Add(candidate.Bookmark.Id);
                    summary.Messages.Add($"  ✗ '{identification.CanonicalTitle}' — no source tags found");
                    continue;
                }

                var finalTags = MergeTags(route.DomainTag, sourceTags);
                if (finalTags.Count == 0)
                {
                    summary.SkippedNoSourceTags++;
                    summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                    {
                        BookmarkId = candidate.Bookmark.Id,
                        Title = candidate.Bookmark.Title,
                        Status = "NoSourceTags",
                        Reason = "Merged tags count was zero."
                    });
                    summary.ProcessedBookmarkIds.Add(candidate.Bookmark.Id);
                    continue;
                }

                candidate.Bookmark.Tags = string.Join(',', finalTags);
                candidate.Bookmark.UpdatedAt = DateTime.UtcNow;
                summary.Tagged++;
                runState.TagsPendingSave++;
                summary.ProcessedBookmarkIds.Add(candidate.Bookmark.Id);
                summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
                {
                    BookmarkId = candidate.Bookmark.Id,
                    Title = candidate.Bookmark.Title,
                    Status = "AiIdentified",
                    Reason = $"Identified by AI as {identification.CanonicalTitle}."
                });
                summary.Messages.Add($"  ✓ '{identification.CanonicalTitle}' tagged: [{string.Join(", ", finalTags)}]");
                runState.TagsPendingSave = await SaveTaggedBookmarksIfNeededAsync(summary, runState.TagsPendingSave, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        if (runState.TagsPendingSave > 0)
            runState.TagsPendingSave = await SaveTaggedBookmarksAsync(summary, runState.TagsPendingSave, cancellationToken).ConfigureAwait(false);

        runState.TotalEligibleCount = totalEligibleCountLocal;
        summary.RemainingCandidates = runState.TotalEligibleCount - summary.ProcessedBookmarkIds.Count;
        summary.HasMore = summary.RemainingCandidates > 0;
        if (summary.Tagged > 0)
            summary.Messages.Add($"Finished with {summary.Tagged} tagged bookmark(s) saved to database.");
        AutoTagRunTelemetry.TryGetCurrent()?.AppendSummaryTo(summary.Messages);
        return summary;
    }

    private async Task<int> SaveTaggedBookmarksIfNeededAsync(
        AiAutoTagSummaryDto summary,
        int tagsPendingSave,
        CancellationToken cancellationToken)
    {
        if (tagsPendingSave < TagSaveBatchSize)
            return tagsPendingSave;

        return await SaveTaggedBookmarksAsync(summary, tagsPendingSave, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> SaveTaggedBookmarksAsync(
        AiAutoTagSummaryDto summary,
        int tagsPendingSave,
        CancellationToken cancellationToken)
    {
        if (tagsPendingSave <= 0)
            return 0;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        summary.Messages.Add($"Saved {tagsPendingSave} tagged bookmark(s) to database.");
        return 0;
    }

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
        var tasks = unique.Select(async request =>
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
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Keep provider results that finished before cancel; apply pass uses partial cache.
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

    private async Task<List<ProviderTagResult>> GetNovelTagsAsync(MediaTagLookupContext context, CancellationToken cancellationToken)
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

    private static RouteDecision ResolveRoute(
        BookmarkNode bookmark,
        string? folderPath,
        AiSeriesIdentification identification)
    {
        var folderDomain = BookmarkTagClassifier.GuessDefaultDomainFromFolderTitle(folderPath ?? string.Empty);
        if (folderDomain is BookmarkTagDomainDto.Anime or BookmarkTagDomainDto.Manga or BookmarkTagDomainDto.Novel)
            return FromDomainDto(folderDomain, identification.SourceHint);

        var urlClassification = BookmarkTagClassifier.Classify(identification.CanonicalTitle, bookmark.Url, folderPath, BookmarkTagDomainDto.Auto);
        if (urlClassification.Domain != BookmarkTagDomain.General)
            return WithMediaSubtype(urlClassification.Domain, identification.SourceHint);

        return FromSourceHint(identification.SourceHint);
    }

    private static RouteDecision ResolveDeterministicRoute(BookmarkNode bookmark, string? folderPath, BookmarkTagDomain domain)
    {
        if (domain != BookmarkTagDomain.Manga)
            return new RouteDecision(domain, domain.ToString());

        var path = folderPath ?? string.Empty;
        var title = bookmark.Title ?? string.Empty;
        if (path.Contains("Manhwa", StringComparison.OrdinalIgnoreCase) || title.Contains("Manhwa", StringComparison.OrdinalIgnoreCase))
            return new RouteDecision(BookmarkTagDomain.Manga, "Manhwa");
        if (path.Contains("Manhua", StringComparison.OrdinalIgnoreCase) || title.Contains("Manhua", StringComparison.OrdinalIgnoreCase))
            return new RouteDecision(BookmarkTagDomain.Manga, "Manhua");
        return new RouteDecision(BookmarkTagDomain.Manga, "Manga");
    }

    private static RouteDecision FromDomainDto(BookmarkTagDomainDto domain, AiSeriesSourceHint sourceHint)
        => domain switch
        {
            BookmarkTagDomainDto.Anime => new RouteDecision(BookmarkTagDomain.Anime, "Anime"),
            BookmarkTagDomainDto.Manga => WithMediaSubtype(BookmarkTagDomain.Manga, sourceHint),
            BookmarkTagDomainDto.Novel => new RouteDecision(BookmarkTagDomain.Novel, "Novel"),
            _ => new RouteDecision(BookmarkTagDomain.General, null)
        };

    private static RouteDecision FromSourceHint(AiSeriesSourceHint sourceHint)
        => sourceHint switch
        {
            AiSeriesSourceHint.Anime => new RouteDecision(BookmarkTagDomain.Anime, "Anime"),
            AiSeriesSourceHint.Manga => new RouteDecision(BookmarkTagDomain.Manga, "Manga"),
            AiSeriesSourceHint.Manhwa => new RouteDecision(BookmarkTagDomain.Manga, "Manhwa"),
            AiSeriesSourceHint.Manhua => new RouteDecision(BookmarkTagDomain.Manga, "Manhua"),
            AiSeriesSourceHint.Novel => new RouteDecision(BookmarkTagDomain.Novel, "Novel"),
            _ => new RouteDecision(BookmarkTagDomain.General, null)
        };

    private static RouteDecision WithMediaSubtype(BookmarkTagDomain domain, AiSeriesSourceHint sourceHint)
    {
        if (domain != BookmarkTagDomain.Manga)
            return new RouteDecision(domain, domain.ToString());

        return sourceHint switch
        {
            AiSeriesSourceHint.Manhwa => new RouteDecision(BookmarkTagDomain.Manga, "Manhwa"),
            AiSeriesSourceHint.Manhua => new RouteDecision(BookmarkTagDomain.Manga, "Manhua"),
            _ => new RouteDecision(BookmarkTagDomain.Manga, "Manga")
        };
    }

    private static List<string> MergeTags(string mediaTypeTag, IEnumerable<string> sourceTags)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Add(mediaTypeTag);
        foreach (var tag in sourceTags)
            Add(tag);

        return merged.Take(MaxTags).ToList();

        void Add(string? tag)
        {
            var trimmed = tag?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return;

            if (IsOtherMediaType(trimmed, mediaTypeTag))
                return;

            if (seen.Add(trimmed))
                merged.Add(trimmed);
        }
    }

    private static bool IsOtherMediaType(string tag, string mediaTypeTag)
    {
        string[] mediaTypes = ["Anime", "Manga", "Manhwa", "Manhua", "Novel"];
        return mediaTypes.Contains(tag, StringComparer.OrdinalIgnoreCase)
            && !tag.Equals(mediaTypeTag, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<Guid> GetDescendantFolderIds(IReadOnlyCollection<BookmarkNode> nodes, Guid folderId)
    {
        var folderIds = new HashSet<Guid> { folderId };
        var childrenByParent = nodes
            .Where(node => node.Type == NodeType.Folder && node.ParentId.HasValue)
            .GroupBy(node => node.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(node => node.Id).ToList());
        var pending = new Queue<Guid>();
        pending.Enqueue(folderId);

        while (pending.Count > 0)
        {
            var parentId = pending.Dequeue();
            if (!childrenByParent.TryGetValue(parentId, out var childIds))
                continue;

            foreach (var childId in childIds)
            {
                if (folderIds.Add(childId))
                    pending.Enqueue(childId);
            }
        }

        return folderIds;
    }

    private static Dictionary<Guid, string> BuildFolderPaths(IEnumerable<BookmarkNode> folders)
    {
        var byId = folders.ToDictionary(folder => folder.Id);
        var paths = new Dictionary<Guid, string>();

        foreach (var folder in byId.Values)
            paths[folder.Id] = BuildPath(folder, byId, paths);

        return paths;
    }

    private static string BuildPath(BookmarkNode folder, IReadOnlyDictionary<Guid, BookmarkNode> folders, Dictionary<Guid, string> cache)
    {
        if (cache.TryGetValue(folder.Id, out var cached))
            return cached;

        var parts = new Stack<string>();
        BookmarkNode? current = folder;
        var seen = new HashSet<Guid>();
        while (current is not null && seen.Add(current.Id))
        {
            if (!string.IsNullOrWhiteSpace(current.Title))
                parts.Push(current.Title.Trim());

            current = current.ParentId.HasValue && folders.TryGetValue(current.ParentId.Value, out var parent)
                ? parent
                : null;
        }

        var path = string.Join(" / ", parts);
        cache[folder.Id] = path;
        return path;
    }

    private static string NormalizeCacheTitle(string title)
        => title.Trim().ToLowerInvariant();

    private sealed record SourceTagLookupKey(BookmarkTagDomain Domain, string CanonicalTitle);

    private sealed record SourceTagLookupRequest(
        SourceTagLookupKey Key,
        string CanonicalTitle,
        string? Url,
        string? FolderPath);

    private sealed class TagFolderRunState
    {
        public int TagsPendingSave { get; set; }
        public int TotalEligibleCount { get; set; }
    }

    private sealed record BookmarkCandidate(BookmarkNode Bookmark, string FolderPath);

    private sealed record RouteDecision(BookmarkTagDomain Domain, string? DomainTag);
}
