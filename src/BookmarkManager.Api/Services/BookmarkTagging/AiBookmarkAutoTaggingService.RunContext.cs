using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.BookmarkTagging;

internal sealed partial class AiBookmarkAutoTaggingService
{
    private sealed record PreparedRunCandidates(
        List<BookmarkCandidate> DeterministicCandidates,
        List<BookmarkCandidate> AmbiguousCandidates,
        int TotalEligibleCount,
        bool IsEmpty);

    private async Task<PreparedRunCandidates?> PrepareRunCandidatesAsync(
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
            return null;
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

        runState.TotalEligibleCount = candidates.Count;

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
            return new PreparedRunCandidates([], [], 0, IsEmpty: true);
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
                deterministicCandidates.Add(candidate);
            else
                ambiguousCandidates.Add(candidate);
        }

        return new PreparedRunCandidates(
            deterministicCandidates,
            ambiguousCandidates,
            candidates.Count,
            IsEmpty: false);
    }
}
