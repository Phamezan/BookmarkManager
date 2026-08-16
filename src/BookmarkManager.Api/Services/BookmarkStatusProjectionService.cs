using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Infrastructure;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services;

public sealed class BookmarkStatusProjectionService : IBookmarkStatusProjectionService
{
    private readonly AppDbContext _db;
    private readonly ILogger<BookmarkStatusProjectionService> _logger;

    public BookmarkStatusProjectionService(AppDbContext db, ILogger<BookmarkStatusProjectionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private static readonly HashSet<string> KnownMediaCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Anime", "Animes", "Manga", "Mangas", "Manhwa", "Manhwas", "Manhua", "Manhuas", "Webtoon", "Webtoons",
        "Novel", "Novels", "Light Novel", "Light Novels", "Web Novel", "Web Novels"
    };

    public async Task<BookmarkNode?> ResolveCategoryRootAsync(BookmarkNode node, CancellationToken ct)
    {
        // Strictly bound to the bookmark's actual ancestor hierarchy.
        // Never query un-related trees or global folders across the database.
        var currentParentId = node.ParentId;
        BookmarkNode? candidateRoot = null;

        while (currentParentId.HasValue)
        {
            var parent = await _db.BookmarkNodes
                .FirstOrDefaultAsync(n => n.Id == currentParentId.Value && !n.IsDeleted, ct);

            if (parent is null)
                break;

            // If the parent is a recognized status folder, its parent is the true category root
            if (BookmarkReadingStatus.StatusFolderNames.Contains(parent.Title, StringComparer.OrdinalIgnoreCase)
                || string.Equals(parent.Title, BookmarkReadingStatus.PlanToRead, StringComparison.OrdinalIgnoreCase))
            {
                if (parent.ParentId.HasValue)
                {
                    var grandParent = await _db.BookmarkNodes
                        .FirstOrDefaultAsync(n => n.Id == parent.ParentId.Value && !n.IsDeleted, ct);
                    if (grandParent is not null && KnownMediaCategories.Contains(grandParent.Title))
                    {
                        return grandParent;
                    }
                }
            }

            if (KnownMediaCategories.Contains(parent.Title))
            {
                candidateRoot = parent;
                break;
            }

            currentParentId = parent.ParentId;
        }

        return candidateRoot;
    }

    public bool IsSupportedCategory(string? category, BookmarkNode? categoryRoot, string? url, string title)
    {
        return categoryRoot is not null && KnownMediaCategories.Contains(categoryRoot.Title);
    }

    public async Task<(BookmarkNode Folder, bool IsNewlyCreated)> ResolveTargetFolderAsync(
        BookmarkNode categoryRoot,
        string targetStatus,
        CancellationToken ct)
    {
        var normStatus = BookmarkReadingStatus.Normalize(targetStatus);
        if (normStatus is null)
        {
            throw new ArgumentException($"Invalid status value: '{targetStatus}'", nameof(targetStatus));
        }

        var folderName = BookmarkReadingStatus.GetStatusFolderName(normStatus);

        // Ongoing returns the category root itself
        if (folderName is null)
        {
            return (categoryRoot, false);
        }

        var existingFolder = await _db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && n.ParentId == categoryRoot.Id && n.Title == folderName && !n.IsDeleted, ct);

        if (existingFolder is not null)
        {
            // If the existing folder has a failed sync state or is missing a pending/confirmed command, ensure command exists
            if (existingFolder.SyncState == SyncState.Failed)
            {
                existingFolder.SyncState = SyncState.Pending;
                existingFolder.Version++;
                existingFolder.UpdatedAt = DateTime.UtcNow;

                var createPayload = new
                {
                    type = "Folder",
                    parentBrowserNodeId = categoryRoot.BrowserNodeId,
                    title = folderName,
                    url = (string?)null,
                    position = existingFolder.Position
                };

                _db.ExtensionCommands.Add(new ExtensionCommandEntry
                {
                    Id = Guid.NewGuid(),
                    OperationId = Guid.NewGuid(),
                    CommandType = "Create",
                    BookmarkId = existingFolder.Id,
                    BrowserNodeId = null,
                    ExpectedVersion = 0,
                    PayloadJson = JsonSerializer.Serialize(createPayload),
                    CreatedAt = DateTime.UtcNow,
                    Status = DeferredCommandHelper.InitialStatus(categoryRoot)
                });
            }

            return (existingFolder, false);
        }

        // Lazy creation of status folder
        var maxPos = await _db.BookmarkNodes
            .Where(n => n.ParentId == categoryRoot.Id && !n.IsDeleted)
            .MaxAsync(n => (int?)n.Position, ct) ?? -1;

        var newFolder = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            ParentId = categoryRoot.Id,
            Type = NodeType.Folder,
            Title = folderName,
            Position = maxPos + 1,
            SyncState = SyncState.Pending,
            Version = 1,
            UpdatedAt = DateTime.UtcNow
        };
        _db.BookmarkNodes.Add(newFolder);

        // Use categoryRoot.BrowserNodeId directly (null if deferred). Never fall back to "1" / browser root!
        var folderCreatePayload = new
        {
            type = "Folder",
            parentBrowserNodeId = categoryRoot.BrowserNodeId,
            title = folderName,
            url = (string?)null,
            position = newFolder.Position
        };

        _db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Create",
            BookmarkId = newFolder.Id,
            BrowserNodeId = null,
            ExpectedVersion = 0,
            PayloadJson = JsonSerializer.Serialize(folderCreatePayload),
            CreatedAt = DateTime.UtcNow,
            Status = DeferredCommandHelper.InitialStatus(categoryRoot)
        });

        _logger.LogInformation("Lazily created status folder '{FolderName}' under '{CategoryRoot}'.", folderName, categoryRoot.Title);
        return (newFolder, true);
    }

    public async Task<BookmarkNode> UpdateStatusAsync(Guid bookmarkId, string targetStatus, CancellationToken ct)
    {
        var normStatus = BookmarkReadingStatus.Normalize(targetStatus);
        if (normStatus is null)
        {
            throw new ArgumentException($"Invalid status value '{targetStatus}'. Must be Ongoing, Plan to Read, Completed, or Dropped.", nameof(targetStatus));
        }

        var node = await _db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.Id == bookmarkId && !n.IsDeleted, ct);

        if (node is null)
        {
            throw new KeyNotFoundException($"Bookmark with ID '{bookmarkId}' not found.");
        }

        if (node.Type == NodeType.Folder)
        {
            throw new InvalidOperationException("Cannot set status on a folder.");
        }

        if (node.IsProtected)
        {
            throw new InvalidOperationException("Cannot modify protected bookmark.");
        }

        var categoryRoot = await ResolveCategoryRootAsync(node, ct);
        if (categoryRoot is null || !IsSupportedCategory(node.Category, categoryRoot, node.Url, node.Title))
        {
            throw new InvalidOperationException("Status folders are only supported for media categories (Anime, Manga, Novel).");
        }

        var (targetFolder, isNewlyCreated) = await ResolveTargetFolderAsync(categoryRoot, normStatus, ct);

        // Idempotency: if already in the desired status and correct folder location,
        // but preserve retry/repair if previously marked Failed!
        var isAlreadyTargetStatus = string.Equals(BookmarkReadingStatus.Normalize(node.Status), normStatus, StringComparison.Ordinal);
        var isAlreadyTargetFolder = node.ParentId == targetFolder.Id;

        if (isAlreadyTargetStatus && isAlreadyTargetFolder && node.SyncState != SyncState.Failed)
        {
            return node;
        }

        var maxPos = await _db.BookmarkNodes
            .Where(n => n.ParentId == targetFolder.Id && !n.IsDeleted && n.Id != node.Id)
            .MaxAsync(n => (int?)n.Position, ct) ?? -1;

        node.Status = normStatus;
        node.ParentId = targetFolder.Id;
        node.Position = maxPos + 1;
        node.SyncState = SyncState.Pending;
        node.Version++;
        node.UpdatedAt = DateTime.UtcNow;

        var movePayload = new
        {
            parentBrowserNodeId = targetFolder.BrowserNodeId,
            position = node.Position
        };

        _db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Move",
            BookmarkId = node.Id,
            BrowserNodeId = node.BrowserNodeId,
            ExpectedVersion = node.Version,
            PayloadJson = JsonSerializer.Serialize(movePayload),
            CreatedAt = DateTime.UtcNow,
            Status = DeferredCommandHelper.InitialStatus(targetFolder)
        });

        await _db.SaveChangesAsync(ct);
        await SyncWebSocketManager.BroadcastSyncAsync();

        _logger.LogInformation("Updated bookmark '{Title}' status to '{Status}' in folder '{Folder}'.", node.Title, normStatus, targetFolder.Title);
        return node;
    }

    public async Task<BulkUpdateBookmarkStatusResponse> BulkUpdateStatusAsync(
        IReadOnlyList<Guid> bookmarkIds,
        string targetStatus,
        CancellationToken ct)
    {
        var response = new BulkUpdateBookmarkStatusResponse();
        if (bookmarkIds.Count == 0)
            return response;

        var normStatus = BookmarkReadingStatus.Normalize(targetStatus);
        if (normStatus is null)
        {
            foreach (var id in bookmarkIds)
            {
                response.Results.Add(new BookmarkStatusItemResult
                {
                    BookmarkId = id,
                    Outcome = BookmarkStatusOutcome.Failed,
                    Message = $"Invalid status value '{targetStatus}'"
                });
            }
            return response;
        }

        var nodes = await _db.BookmarkNodes
            .Where(n => bookmarkIds.Contains(n.Id))
            .ToListAsync(ct);

        var nodeById = nodes.ToDictionary(n => n.Id);

        // Local caches for folder resolution and position tracking during bulk update
        var categoryRootCache = new Dictionary<Guid, BookmarkNode?>();
        var folderCache = new Dictionary<string, BookmarkNode>();
        var positionCache = new Dictionary<Guid, int>();

        var anyChanged = false;

        foreach (var id in bookmarkIds)
        {
            if (!nodeById.TryGetValue(id, out var node) || node.IsDeleted)
            {
                response.Results.Add(new BookmarkStatusItemResult
                {
                    BookmarkId = id,
                    Outcome = BookmarkStatusOutcome.Skipped,
                    Message = "Bookmark not found or deleted"
                });
                continue;
            }

            if (node.Type == NodeType.Folder)
            {
                response.Results.Add(new BookmarkStatusItemResult
                {
                    BookmarkId = id,
                    Outcome = BookmarkStatusOutcome.Skipped,
                    Message = "Cannot set status on a folder"
                });
                continue;
            }

            if (node.IsProtected)
            {
                response.Results.Add(new BookmarkStatusItemResult
                {
                    BookmarkId = id,
                    Outcome = BookmarkStatusOutcome.Skipped,
                    Message = "Protected bookmark cannot be modified"
                });
                continue;
            }

            BookmarkNode? categoryRoot;
            if (node.ParentId.HasValue && categoryRootCache.TryGetValue(node.ParentId.Value, out var cachedRoot))
            {
                categoryRoot = cachedRoot;
            }
            else
            {
                categoryRoot = await ResolveCategoryRootAsync(node, ct);
                if (node.ParentId.HasValue)
                {
                    categoryRootCache[node.ParentId.Value] = categoryRoot;
                }
            }

            if (categoryRoot is null || !IsSupportedCategory(node.Category, categoryRoot, node.Url, node.Title))
            {
                response.Results.Add(new BookmarkStatusItemResult
                {
                    BookmarkId = id,
                    Outcome = BookmarkStatusOutcome.Skipped,
                    Message = "Bookmark does not belong to a supported media category"
                });
                continue;
            }

            // Resolve target folder with cache
            var cacheKey = $"{categoryRoot.Id}:{normStatus}";
            BookmarkNode targetFolder;
            if (folderCache.TryGetValue(cacheKey, out var cachedTargetFolder))
            {
                targetFolder = cachedTargetFolder;
            }
            else
            {
                var (resolvedFolder, _) = await ResolveTargetFolderAsync(categoryRoot, normStatus, ct);
                targetFolder = resolvedFolder;
                folderCache[cacheKey] = targetFolder;
            }

            var previousNormStatus = BookmarkReadingStatus.Normalize(node.Status);
            var isAlreadyCorrect = string.Equals(previousNormStatus, normStatus, StringComparison.Ordinal)
                && node.ParentId == targetFolder.Id;

            if (isAlreadyCorrect && node.SyncState != SyncState.Failed)
            {
                response.Results.Add(new BookmarkStatusItemResult
                {
                    BookmarkId = id,
                    Outcome = BookmarkStatusOutcome.AlreadyCorrect,
                    PreviousStatus = node.Status,
                    CurrentStatus = node.Status,
                    Message = $"Already in status '{normStatus}'"
                });
                continue;
            }

            // Calculate next position in target folder
            if (!positionCache.TryGetValue(targetFolder.Id, out var nextPos))
            {
                var maxPos = await _db.BookmarkNodes
                    .Where(n => n.ParentId == targetFolder.Id && !n.IsDeleted)
                    .MaxAsync(n => (int?)n.Position, ct) ?? -1;
                nextPos = maxPos + 1;
            }

            var previousStatus = node.Status;
            node.Status = normStatus;
            node.ParentId = targetFolder.Id;
            node.Position = nextPos;
            node.SyncState = SyncState.Pending;
            node.Version++;
            node.UpdatedAt = DateTime.UtcNow;

            positionCache[targetFolder.Id] = nextPos + 1;

            var movePayload = new
            {
                parentBrowserNodeId = targetFolder.BrowserNodeId,
                position = node.Position
            };

            _db.ExtensionCommands.Add(new ExtensionCommandEntry
            {
                Id = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                CommandType = "Move",
                BookmarkId = node.Id,
                BrowserNodeId = node.BrowserNodeId,
                ExpectedVersion = node.Version,
                PayloadJson = JsonSerializer.Serialize(movePayload),
                CreatedAt = DateTime.UtcNow,
                Status = DeferredCommandHelper.InitialStatus(targetFolder)
            });

            anyChanged = true;
            response.Results.Add(new BookmarkStatusItemResult
            {
                BookmarkId = id,
                Outcome = BookmarkStatusOutcome.Changed,
                PreviousStatus = previousStatus,
                CurrentStatus = normStatus,
                Message = $"Updated status to '{normStatus}'"
            });
        }

        if (anyChanged)
        {
            await _db.SaveChangesAsync(ct);
            await SyncWebSocketManager.BroadcastSyncAsync();
            _logger.LogInformation("Bulk updated {ChangedCount} bookmarks to status '{Status}'.", response.ChangedCount, normStatus);
        }

        return response;
    }

    public async Task<RelatedSeriesPreviewResponse> GetRelatedStatusCandidatesAsync(
        Guid bookmarkId,
        string targetStatus,
        CancellationToken ct)
    {
        var normTargetStatus = BookmarkReadingStatus.Normalize(targetStatus) ?? targetStatus;

        var sourceNode = await _db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.Id == bookmarkId && !n.IsDeleted, ct);

        if (sourceNode is null)
        {
            throw new KeyNotFoundException($"Bookmark with ID '{bookmarkId}' not found.");
        }

        var categoryRoot = await ResolveCategoryRootAsync(sourceNode, ct);
        var sourceCategory = sourceNode.Category ?? categoryRoot?.Title;

        var response = new RelatedSeriesPreviewResponse
        {
            SourceBookmarkId = sourceNode.Id,
            SourceTitle = sourceNode.Title,
            SourceCategory = sourceCategory,
            TargetStatus = normTargetStatus
        };

        // If no supported category root exists, do NOT perform an unscoped query across all bookmarks.
        if (categoryRoot is null)
        {
            return response;
        }

        // Scope query strictly to the same category root or direct status child folders
        var rootFolderId = categoryRoot.Id;
        var childFolderIds = await _db.BookmarkNodes
            .Where(n => n.Type == NodeType.Folder && n.ParentId == rootFolderId && !n.IsDeleted)
            .Select(n => n.Id)
            .ToListAsync(ct);

        var relevantFolderIds = childFolderIds.Concat([rootFolderId]).ToHashSet();
        var candidateNodes = await _db.BookmarkNodes
            .Include(n => n.Parent)
            .Where(n => n.Id != bookmarkId && !n.IsDeleted && n.Type == NodeType.Bookmark && n.ParentId != null && relevantFolderIds.Contains(n.ParentId.Value))
            .ToListAsync(ct);

        foreach (var candidate in candidateNodes)
        {
            // Skip bookmarks that already have the target status
            var candNormStatus = BookmarkReadingStatus.Normalize(candidate.Status);
            if (string.Equals(candNormStatus, normTargetStatus, StringComparison.Ordinal))
            {
                continue;
            }

            var matchResult = RelatedSeriesMatcher.Match(sourceNode.Title, candidate.Title);
            if (matchResult.IsMatch)
            {
                response.Candidates.Add(new RelatedSeriesCandidateDto
                {
                    Id = candidate.Id,
                    Title = candidate.Title,
                    Url = candidate.Url,
                    CurrentStatus = candidate.Status ?? BookmarkReadingStatus.Ongoing,
                    Category = candidate.Category ?? categoryRoot.Title,
                    ParentFolderName = candidate.Parent?.Title,
                    MatchScore = matchResult.Score,
                    MatchReason = matchResult.Reason,
                    Selected = true
                });
            }
        }

        response.Candidates = response.Candidates
            .OrderByDescending(c => c.MatchScore)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return response;
    }
}
