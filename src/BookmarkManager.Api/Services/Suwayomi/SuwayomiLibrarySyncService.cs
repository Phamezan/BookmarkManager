using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>
/// Mirrors the user's Suwayomi library into BookmarkManager: adds a bookmark when a series is added
/// in the Suwayomi WebUI, soft-deletes the linked bookmark(s) when a series is removed, and moves
/// progress forward as the user reads. Polls every <see cref="SuwayomiOptions.LibrarySyncIntervalMinutes"/>
/// and delegates every decision to the pure <see cref="SuwayomiLibrarySyncPlanner"/>.
/// </summary>
public sealed class SuwayomiLibrarySyncService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<SuwayomiOptions> _options;
    private readonly SuwayomiImportBackgroundJob _importJob;
    private readonly ILogger<SuwayomiLibrarySyncService> _logger;

    /// <summary>Manga ids the previous cycle saw as unlinked; drives the two-cycle debounce.</summary>
    private IReadOnlySet<int> _previouslyUnlinked = new HashSet<int>();

    public SuwayomiLibrarySyncService(
        IServiceScopeFactory scopeFactory,
        IOptions<SuwayomiOptions> options,
        SuwayomiImportBackgroundJob importJob,
        ILogger<SuwayomiLibrarySyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _importJob = importJob;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Suwayomi library sync background service started.");

        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_options.Value.LibrarySyncEnabled)
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Suwayomi library sync run failed.");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _options.Value.LibrarySyncIntervalMinutes)), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Runs one poll. Internal so a test can drive a single cycle.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        if (_importJob.IsRunning)
        {
            _logger.LogInformation("Suwayomi library sync skipped: a Suwayomi import is running.");
            return;
        }

        IReadOnlyList<SuwayomiLibraryManga> library;
        using (var scope = _scopeFactory.CreateScope())
        {
            var client = scope.ServiceProvider.GetRequiredService<ISuwayomiClient>();
            try
            {
                library = await client.GetLibraryAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Suwayomi library sync skipped: the library fetch failed.");
                return;
            }
        }

        await ApplyAsync(library, ct).ConfigureAwait(false);
    }

    private async Task ApplyAsync(IReadOnlyList<SuwayomiLibraryManga> library, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var options = _options.Value;
        var baseUrl = options.PublicBaseUrl.TrimEnd('/');

        BookmarkNode? syncFolder = null;
        if (options.LibrarySyncFolderId is { } folderId)
        {
            syncFolder = await db.BookmarkNodes
                .FirstOrDefaultAsync(n => n.Id == folderId && n.Type == NodeType.Folder && !n.IsDeleted, ct)
                .ConfigureAwait(false);
            if (syncFolder is null)
            {
                _logger.LogWarning(
                    "Suwayomi library sync: folder {FolderId} does not exist or is deleted; creations are skipped.",
                    folderId);
            }
        }
        else
        {
            _logger.LogWarning("Suwayomi library sync: LibrarySyncFolderId is not set; creations are skipped.");
        }

        var bookmarks = await LoadBookmarksAsync(db, baseUrl, ct).ConfigureAwait(false);

        var plan = SuwayomiLibrarySyncPlanner.Plan(
            library,
            bookmarks,
            _previouslyUnlinked,
            syncFolder is not null,
            baseUrl,
            options.LibrarySyncMaxRemovalsPerCycle);

        if (plan.LibraryEmpty)
        {
            _logger.LogWarning("Suwayomi library sync: the library is empty; removals are suppressed.");
        }
        else if (plan.RemovalsSuppressed > 0)
        {
            _logger.LogWarning(
                "Suwayomi library sync: {Count} removals exceed the per-cycle cap of {Cap}; removals are suppressed.",
                plan.RemovalsSuppressed, options.LibrarySyncMaxRemovalsPerCycle);
        }

        var changed = plan.Creations.Count > 0 || plan.Links.Count > 0 || plan.Relinks.Count > 0
            || plan.Removals.Count > 0 || plan.ProgressUpdates.Count > 0;

        if (changed)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await ApplyPlanAsync(db, plan, syncFolder, baseUrl, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        _previouslyUnlinked = plan.Unlinked;

        if (changed)
        {
            await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync().ConfigureAwait(false);
            _logger.LogInformation(
                "Suwayomi library sync: created {Created}, linked {Linked}, relinked {Relinked}, removed {Removed}, progress {Progress}.",
                plan.Creations.Count, plan.Links.Count, plan.Relinks.Count, plan.Removals.Count, plan.ProgressUpdates.Count);
        }
    }

    private static async Task<IReadOnlyList<SuwayomiLibrarySyncBookmark>> LoadBookmarksAsync(
        AppDbContext db, string baseUrl, CancellationToken ct)
    {
        var linked = await db.BookmarkNodes
            .AsNoTracking()
            .Where(n => n.SuwayomiMangaId != null)
            .Select(n => new SuwayomiLibrarySyncBookmark(
                n.Id, n.Title, n.Url, n.SuwayomiMangaId, n.CurrentProgress, n.IsDeleted))
            .ToListAsync(ct).ConfigureAwait(false);

        var prefix = $"{baseUrl}/manga/";
        var urlMatches = await db.BookmarkNodes
            .AsNoTracking()
            .Where(n => !n.IsDeleted
                && n.SuwayomiMangaId == null
                && n.Url != null
                && n.Url.StartsWith(prefix))
            .Select(n => new SuwayomiLibrarySyncBookmark(
                n.Id, n.Title, n.Url, n.SuwayomiMangaId, n.CurrentProgress, n.IsDeleted))
            .ToListAsync(ct).ConfigureAwait(false);

        return linked.Concat(urlMatches).ToList();
    }

    private static async Task ApplyPlanAsync(
        AppDbContext db,
        SuwayomiLibrarySyncPlan plan,
        BookmarkNode? syncFolder,
        string baseUrl,
        CancellationToken ct)
    {
        foreach (var creation in plan.Creations)
        {
            var node = new BookmarkNode
            {
                Id = Guid.NewGuid(),
                ParentId = syncFolder!.Id,
                Type = NodeType.Bookmark,
                Title = BookmarkNodeWriter.ClampTitle(creation.Title),
                Url = creation.Url,
                SuwayomiMangaId = creation.MangaId,
                SourceUrl = creation.SourceUrl,
                CurrentProgress = creation.CurrentProgress,
                SyncState = SyncState.Pending,
                Version = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await BookmarkNodeWriter.AppendNewBookmarkAsync(db, node, syncFolder, ct).ConfigureAwait(false);
        }

        if (plan.Links.Count > 0)
        {
            var linkIds = plan.Links.Select(l => l.BookmarkId).ToHashSet();
            var nodes = await db.BookmarkNodes.Where(n => linkIds.Contains(n.Id)).ToListAsync(ct).ConfigureAwait(false);
            var byId = nodes.ToDictionary(n => n.Id);
            foreach (var link in plan.Links)
            {
                if (byId.TryGetValue(link.BookmarkId, out var node))
                {
                    node.SuwayomiMangaId = link.MangaId;
                    node.SourceUrl = link.SourceUrl;
                    node.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        if (plan.Relinks.Count > 0)
        {
            var relinkIds = plan.Relinks.Select(r => r.BookmarkId).ToHashSet();
            var nodes = await db.BookmarkNodes
                .Where(n => relinkIds.Contains(n.Id) && !n.IsDeleted)
                .ToListAsync(ct).ConfigureAwait(false);
            var byId = nodes.ToDictionary(n => n.Id);
            foreach (var relink in plan.Relinks)
            {
                if (!byId.TryGetValue(relink.BookmarkId, out var node))
                {
                    continue;
                }

                node.SuwayomiMangaId = relink.MangaId;
                node.SourceUrl = relink.SourceUrl;

                var newUrl = $"{baseUrl}/manga/{relink.MangaId}";
                if (!string.Equals(node.Url, newUrl, StringComparison.Ordinal))
                {
                    node.Url = newUrl;
                    BookmarkNodeWriter.EnqueueUpdateCommand(db, node);
                }
                else
                {
                    node.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        if (plan.Removals.Count > 0)
        {
            var removeIds = plan.Removals.ToHashSet();
            var nodes = await db.BookmarkNodes
                .Where(n => removeIds.Contains(n.Id) && !n.IsDeleted)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var node in nodes)
            {
                await BookmarkNodeWriter.ApplySoftDeleteAsync(db, node, ct).ConfigureAwait(false);
            }
        }

        if (plan.ProgressUpdates.Count > 0)
        {
            var progressIds = plan.ProgressUpdates.Select(p => p.BookmarkId).ToHashSet();
            var nodes = await db.BookmarkNodes.Where(n => progressIds.Contains(n.Id)).ToListAsync(ct).ConfigureAwait(false);
            var byId = nodes.ToDictionary(n => n.Id);
            foreach (var update in plan.ProgressUpdates)
            {
                if (byId.TryGetValue(update.BookmarkId, out var node))
                {
                    node.CurrentProgress = update.Progress;
                    node.UpdatedAt = DateTime.UtcNow;
                }
            }
        }
    }
}
