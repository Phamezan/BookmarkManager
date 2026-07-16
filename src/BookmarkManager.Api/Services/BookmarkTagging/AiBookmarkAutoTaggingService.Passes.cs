using System.Collections.Concurrent;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.BookmarkTagging;

internal sealed partial class AiBookmarkAutoTaggingService
{
    private async Task ProcessDeterministicPassAsync(
        IReadOnlyList<BookmarkCandidate> deterministicCandidates,
        ConcurrentDictionary<SourceTagLookupKey, List<ProvenanceTagEntry>> sourceTagCache,
        ConcurrentDictionary<SourceTagLookupKey, byte> providerFailedKeys,
        AiAutoTagSummaryDto summary,
        TagFolderRunState runState,
        CancellationToken cancellationToken)
    {
        if (deterministicCandidates.Count == 0)
            return;

        summary.Messages.Add($"Deterministic pass: processing {deterministicCandidates.Count} obvious candidate(s) without AI.");

        var prepared = deterministicCandidates
            .Select(candidate =>
            {
                var classification = BookmarkMediaCandidateClassifier.Classify(
                    candidate.Bookmark.Title,
                    candidate.Bookmark.Url,
                    candidate.FolderPath);
                var route = ResolveDeterministicRoute(candidate.Bookmark, candidate.FolderPath, classification.Domain);
                return new DeterministicPreparedCandidate(
                    candidate,
                    classification,
                    route,
                    new SourceTagLookupKey(route.Domain, NormalizeCacheTitle(classification.CanonicalTitle)));
            })
            .ToList();

        await PrefetchSourceTagsAsync(
                prepared.Select(item => new SourceTagLookupRequest(
                    item.CacheKey,
                    item.Classification.CanonicalTitle,
                    item.Candidate.Bookmark.Url,
                    item.Candidate.FolderPath)).ToList(),
                sourceTagCache,
                providerFailedKeys,
                summary,
                runState.BypassProviderCache,
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in prepared)
        {
            await ApplyCachedTagsAsync(
                    new TagApplyRequest(
                        item.Candidate,
                        item.CacheKey,
                        item.Classification.CanonicalTitle,
                        "DeterministicClassified",
                        $"Classified deterministically as {item.Classification.Domain}.",
                        item.Classification.CanonicalTitle,
                        Confidence: null),
                    item.Route.DomainTag!,
                    sourceTagCache,
                    providerFailedKeys,
                    summary,
                    runState,
                    cancellationToken)
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
                break;
        }
    }

    private async Task ProcessAiPassAsync(
        IReadOnlyList<BookmarkCandidate> ambiguousCandidates,
        ConcurrentDictionary<SourceTagLookupKey, List<ProvenanceTagEntry>> sourceTagCache,
        ConcurrentDictionary<SourceTagLookupKey, byte> providerFailedKeys,
        AiAutoTagSummaryDto summary,
        TagFolderRunState runState,
        CancellationToken cancellationToken)
    {
        if (ambiguousCandidates.Count == 0)
            return;

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
        RecordAiIdentificationFailures(ambiguousCandidates, identificationSummary, summary);

        var byBookmarkId = ambiguousCandidates.ToDictionary(candidate => candidate.Bookmark.Id);
        var aiApplyItems = BuildAiApplyItems(identificationSummary, byBookmarkId, summary);
        if (aiApplyItems.Lookups.Count > 0)
        {
            await PrefetchSourceTagsAsync(
                    aiApplyItems.Lookups,
                    sourceTagCache,
                    providerFailedKeys,
                    summary,
                    runState.BypassProviderCache,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var applyItem in aiApplyItems.ApplyItems)
        {
            var cacheKey = new SourceTagLookupKey(
                applyItem.Route.Domain,
                NormalizeCacheTitle(applyItem.Identification.CanonicalTitle));

            await ApplyCachedTagsAsync(
                    new TagApplyRequest(
                        applyItem.Candidate,
                        cacheKey,
                        applyItem.Identification.CanonicalTitle,
                        "AiIdentified",
                        $"Identified by AI as {applyItem.Identification.CanonicalTitle}.",
                        applyItem.Identification.CanonicalTitle,
                        Confidence: applyItem.Identification.Confidence),
                    applyItem.Route.DomainTag!,
                    sourceTagCache,
                    providerFailedKeys,
                    summary,
                    runState,
                    cancellationToken)
                .ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
                break;
        }
    }

    private async Task ApplyCachedTagsAsync(
        TagApplyRequest request,
        string domainTag,
        ConcurrentDictionary<SourceTagLookupKey, List<ProvenanceTagEntry>> sourceTagCache,
        ConcurrentDictionary<SourceTagLookupKey, byte> providerFailedKeys,
        AiAutoTagSummaryDto summary,
        TagFolderRunState runState,
        CancellationToken cancellationToken)
    {
        sourceTagCache.TryGetValue(request.CacheKey, out var sourceTags);
        sourceTags ??= [];

        if (providerFailedKeys.ContainsKey(request.CacheKey))
        {
            AddBookmarkStatus(request, summary, "ProviderFailed", "Provider lookup threw an exception.");
            return;
        }

        if (!sourceTags.Any(tag => !string.IsNullOrWhiteSpace(tag.Tag)))
        {
            summary.SkippedNoSourceTags++;
            AddBookmarkStatus(request, summary, "NoSourceTags", "No source tags found on providers.");
            summary.Messages.Add($"  ✗ '{request.NoTagsLogTitle}' — no source tags found");
            return;
        }

        var finalTags = MergeTags(domainTag, sourceTags);
        if (finalTags.Count == 0)
        {
            summary.SkippedNoSourceTags++;
            AddBookmarkStatus(request, summary, "NoSourceTags", "Merged tags count was zero.");
            return;
        }

        // Suggestion-not-mutation: never rename during AI tagging. Surface SuggestedTitle for
        // client review UIs; Results & Reruns is tag-edit only so Accept is not wired there.
        var suggestedTitle = BookmarkTitleSuggestionBuilder.Build(
            request.CanonicalTitle,
            request.Candidate.Bookmark.Title,
            request.Candidate.Bookmark.Url);

        request.Candidate.Bookmark.Tags = string.Join(',', finalTags.Select(t => t.Tag));
        request.Candidate.Bookmark.UpdatedAt = DateTime.UtcNow;

        TagProvenanceWriter.Replace(
            _db,
            request.Candidate.Bookmark.Id,
            finalTags.Select(entry => (entry.Tag, entry.Provider)),
            request.Confidence);

        summary.Tagged++;
        runState.TagsPendingSave++;
        summary.ProcessedBookmarkIds.Add(request.Candidate.Bookmark.Id);
        summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
        {
            BookmarkId = request.Candidate.Bookmark.Id,
            Title = request.Candidate.Bookmark.Title,
            Status = request.SuccessStatus,
            Reason = request.SuccessReason,
            Tags = request.Candidate.Bookmark.Tags,
            SuggestedTitle = suggestedTitle
        });
        summary.Messages.Add($"  ✓ '{request.CanonicalTitle}' tagged: [{string.Join(", ", finalTags.Select(t => t.Tag))}]");
        runState.TagsPendingSave = await SaveTaggedBookmarksIfNeededAsync(summary, runState.TagsPendingSave, cancellationToken).ConfigureAwait(false);
    }

    private static void AddBookmarkStatus(
        TagApplyRequest request,
        AiAutoTagSummaryDto summary,
        string status,
        string reason)
    {
        summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
        {
            BookmarkId = request.Candidate.Bookmark.Id,
            Title = request.Candidate.Bookmark.Title,
            Status = status,
            Reason = reason
        });
        summary.ProcessedBookmarkIds.Add(request.Candidate.Bookmark.Id);
    }

    private static void RecordAiIdentificationFailures(
        IReadOnlyList<BookmarkCandidate> ambiguousCandidates,
        AiSeriesIdentificationSummary identificationSummary,
        AiAutoTagSummaryDto summary)
    {
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

        var identifiedBookmarkIds = identificationSummary.Items.Select(item => item.Id).ToHashSet();
        foreach (var candidate in ambiguousCandidates.Where(ac => !identifiedBookmarkIds.Contains(ac.Bookmark.Id)))
        {
            summary.BookmarkStatuses.Add(new AiAutoTagBookmarkStatusDto
            {
                BookmarkId = candidate.Bookmark.Id,
                Title = candidate.Bookmark.Title,
                Status = identificationSummary.IsRateLimited
                    ? "RateLimited"
                    : identificationSummary.FailedChunks > 0 ? "AiInvalidResponse" : "AiPendingRetry",
                Reason = identificationSummary.IsRateLimited
                    ? "Rate limit hit on AI provider."
                    : identificationSummary.FailedChunks > 0
                        ? "AI returned an invalid response structure."
                        : "AI identification did not succeed for this item."
            });
        }
    }

    private AiApplyPlan BuildAiApplyItems(
        AiSeriesIdentificationSummary identificationSummary,
        IReadOnlyDictionary<Guid, BookmarkCandidate> byBookmarkId,
        AiAutoTagSummaryDto summary)
    {
        var lookups = new List<SourceTagLookupRequest>();
        var applyItems = new List<AiApplyItem>();

        foreach (var identification in identificationSummary.Items)
        {
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

            lookups.Add(new SourceTagLookupRequest(
                new SourceTagLookupKey(route.Domain, NormalizeCacheTitle(identification.CanonicalTitle)),
                identification.CanonicalTitle,
                candidate.Bookmark.Url,
                candidate.FolderPath));
            applyItems.Add(new AiApplyItem(identification, candidate, route));
        }

        return new AiApplyPlan(lookups, applyItems);
    }
}
