using System.Collections.Concurrent;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.BookmarkTagging;

internal sealed partial class AiBookmarkAutoTaggingService
{
    public async Task<AiAutoTagSummaryDto> RerunBookmarksAsync(
        IReadOnlyCollection<Guid> bookmarkIds,
        CancellationToken cancellationToken)
    {
        using var telemetryScope = AutoTagRunTelemetry.BeginScope();
        var summary = new AiAutoTagSummaryDto();
        // Bypass provider TTL caches: rerunning a NoSourceTags/ProviderFailed item
        // must hit the providers again instead of replaying the cached empty result.
        var runState = new TagFolderRunState { BypassProviderCache = true };
        var runCanceled = false;

        try
        {
            await RerunBookmarksCoreAsync(bookmarkIds, summary, runState, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            runCanceled = true;
        }

        if (cancellationToken.IsCancellationRequested)
            runCanceled = true;

        if (runState.TagsPendingSave > 0)
        {
            await SaveTaggedBookmarksAsync(summary, runState.TagsPendingSave, CancellationToken.None).ConfigureAwait(false);
            runState.TagsPendingSave = 0;
        }

        summary.RemainingCandidates = 0;
        summary.HasMore = false;
        if (runCanceled)
            summary.Messages.Add("Rerun canceled; saved tagged bookmarks completed before cancel.");
        telemetryScope.AppendSummaryTo(summary.Messages);
        PopulateProviderTimings(summary, telemetryScope);
        return summary;
    }

    private async Task RerunBookmarksCoreAsync(
        IReadOnlyCollection<Guid> bookmarkIds,
        AiAutoTagSummaryDto summary,
        TagFolderRunState runState,
        CancellationToken cancellationToken)
    {
        var uniqueBookmarkIds = bookmarkIds.Distinct().ToList();
        var folders = await _db.BookmarkNodes
            .Where(node => !node.IsDeleted && node.Type == NodeType.Folder)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var requestedBookmarks = await _db.BookmarkNodes
            .Where(node => !node.IsDeleted && node.Type == NodeType.Bookmark && uniqueBookmarkIds.Contains(node.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var folderPaths = BuildFolderPaths(folders);
        var bookmarkLookup = requestedBookmarks.ToDictionary(node => node.Id);

        var candidates = new List<BookmarkCandidate>();
        foreach (var bookmarkId in uniqueBookmarkIds)
        {
            if (!bookmarkLookup.TryGetValue(bookmarkId, out var bookmark))
            {
                summary.Messages.Add($"Bookmark {bookmarkId} not found or deleted; skipping.");
                continue;
            }

            candidates.Add(new BookmarkCandidate(
                bookmark,
                folderPaths.GetValueOrDefault(bookmark.ParentId ?? Guid.Empty, string.Empty)));
        }

        if (candidates.Count == 0)
        {
            summary.Messages.Add("No valid bookmarks to rerun.");
            return;
        }

        summary.TotalCandidates = candidates.Count;
        summary.Messages.Add($"Rerunning {candidates.Count} bookmark(s).");

        var deterministicCandidates = new List<BookmarkCandidate>();
        var ambiguousCandidates = new List<BookmarkCandidate>();
        foreach (var candidate in candidates)
        {
            var classification = BookmarkMediaCandidateClassifier.Classify(
                candidate.Bookmark.Title,
                candidate.Bookmark.Url,
                candidate.FolderPath);

            if (!classification.RequiresAi)
                deterministicCandidates.Add(candidate);
            else
                ambiguousCandidates.Add(candidate);
        }

        runState.TotalEligibleCount = candidates.Count;
        var sourceTagCache = new ConcurrentDictionary<SourceTagLookupKey, List<ProvenanceTagEntry>>();
        var providerFailedKeys = new ConcurrentDictionary<SourceTagLookupKey, byte>();

        await ProcessDeterministicPassAsync(
                deterministicCandidates,
                sourceTagCache,
                providerFailedKeys,
                summary,
                runState,
                cancellationToken)
            .ConfigureAwait(false);

        if (!summary.StopForRateLimit)
        {
            await ProcessAiPassAsync(
                    ambiguousCandidates,
                    sourceTagCache,
                    providerFailedKeys,
                    summary,
                    runState,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (runState.TagsPendingSave > 0)
            runState.TagsPendingSave = await SaveTaggedBookmarksAsync(summary, runState.TagsPendingSave, cancellationToken).ConfigureAwait(false);

        if (summary.Tagged > 0 && !cancellationToken.IsCancellationRequested)
            summary.Messages.Add($"Finished rerun with {summary.Tagged} tagged bookmark(s) saved to database.");
    }
}
