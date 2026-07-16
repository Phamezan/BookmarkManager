using BookmarkManager.Api.Data;
using BookmarkManager.Api.Infrastructure;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services;

public sealed class AutoTaggerService
{
    private const int BatchSize = 50;

    private readonly AppDbContext _db;
    private readonly BookmarkTaggingService _bookmarkTagging;
    private readonly ILogger<AutoTaggerService> _logger;

    public AutoTaggerService(
        AppDbContext db,
        BookmarkTaggingService bookmarkTagging,
        ILogger<AutoTaggerService> logger)
    {
        _db = db;
        _bookmarkTagging = bookmarkTagging;
        _logger = logger;
    }

    public async Task<RetagAllResult> ProcessUntaggedAsync(CancellationToken ct)
        => await ProcessAsync(overwrite: false, folderIds: null, ct).ConfigureAwait(false);

    public async Task<RetagAllResult> ProcessAsync(bool overwrite, IReadOnlyCollection<Guid>? folderIds, CancellationToken ct)
    {
        var query = _db.BookmarkNodes
            .Where(n => !n.IsDeleted && n.Type == NodeType.Bookmark);

        if (!overwrite)
            query = query.Where(n => n.Tags == null || n.Tags == string.Empty);

        if (folderIds is { Count: > 0 })
            query = query.Where(n => n.ParentId.HasValue && folderIds.Contains(n.ParentId.Value));

        var candidates = await query
            .OrderBy(n => n.ParentId)
            .ThenBy(n => n.Position)
            .Select(n => new AutoTagCandidate(n.Id, n.ParentId, n.Title, n.Url, n.Tags))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new RetagAllResult { Total = candidates.Count };
        if (candidates.Count == 0)
        {
            _logger.LogInformation("Auto tagger found no bookmarks requiring tags.");
            return result;
        }

        var changed = 0;
        foreach (var group in candidates.GroupBy(c => c.ParentId))
        {
            ct.ThrowIfCancellationRequested();
            var folderPath = await FolderHierarchy.BuildFolderPathAsync(_db, group.Key, ct).ConfigureAwait(false);
            var requestedDomain = BookmarkTagClassifier.GuessDefaultDomainFromFolderTitle(folderPath ?? string.Empty);
            var folderCandidates = group.ToList();

            _logger.LogInformation(
                "Auto tagging {Count} bookmarks in folder path '{FolderPath}' as {Domain}.",
                folderCandidates.Count,
                folderPath ?? "<root>",
                requestedDomain);

            for (var offset = 0; offset < folderCandidates.Count; offset += BatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = folderCandidates.Skip(offset).Take(BatchSize).ToList();
                var requestItems = batch
                    .Select(item => new BookmarkTagCandidateDto
                    {
                        Id = item.Id,
                        Title = item.Title,
                        Url = item.Url
                    })
                    .ToList();

                var generated = await _bookmarkTagging
                    .GetTagsForBatchAsync(requestItems, folderPath, requestedDomain, ct)
                    .ConfigureAwait(false);

                foreach (var item in batch)
                {
                    if (!overwrite && !string.IsNullOrWhiteSpace(item.Tags))
                    {
                        result.Skipped++;
                        continue;
                    }

                    if (!generated.Tags.TryGetValue(item.Id, out var tags) || tags.Count == 0)
                    {
                        result.Skipped++;
                        continue;
                    }

                    var node = await _db.BookmarkNodes.FirstAsync(n => n.Id == item.Id, ct).ConfigureAwait(false);
                    node.Tags = string.Join(",", tags);
                    node.UpdatedAt = DateTime.UtcNow;
                    result.Tagged++;
                    changed++;
                }

                if (changed > 0)
                {
                    await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                    changed = 0;
                }
            }
        }

        if (result.Tagged > 0)
            await SyncWebSocketManager.BroadcastSyncAsync().ConfigureAwait(false);

        _logger.LogInformation(
            "Auto tagger completed. Tagged {Tagged}, skipped {Skipped}, total {Total}.",
            result.Tagged,
            result.Skipped,
            result.Total);

        return result;
    }



    private sealed record AutoTagCandidate(Guid Id, Guid? ParentId, string Title, string? Url, string? Tags);
}
