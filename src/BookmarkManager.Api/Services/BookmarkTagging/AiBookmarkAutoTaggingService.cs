using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace BookmarkManager.Api.Services.BookmarkTagging;

internal sealed partial class AiBookmarkAutoTaggingService
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
    private readonly ICatalogTagProvider _catalog;
    private readonly ILogger<AiBookmarkAutoTaggingService> _logger;

    public AiBookmarkAutoTaggingService(
        AppDbContext db,
        AiSeriesIdentifierService identifier,
        IAnilistTagProvider anilist,
        IMangaUpdatesTagProvider mangaUpdates,
        IKitsuTagProvider kitsu,
        INovelFullTagProvider novelFull,
        ICatalogTagProvider catalog,
        ILogger<AiBookmarkAutoTaggingService> logger)
    {
        _db = db;
        _identifier = identifier;
        _anilist = anilist;
        _mangaUpdates = mangaUpdates;
        _kitsu = kitsu;
        _novelFull = novelFull;
        _catalog = catalog;
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
            await TagFolderCoreAsync(
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

        if (cancellationToken.IsCancellationRequested)
            runCanceled = true;

        if (runState.TagsPendingSave > 0)
        {
            await SaveTaggedBookmarksAsync(summary, runState.TagsPendingSave, CancellationToken.None).ConfigureAwait(false);
            runState.TagsPendingSave = 0;
        }

        summary.RemainingCandidates = Math.Max(0, runState.TotalEligibleCount - summary.ProcessedBookmarkIds.Count);
        summary.HasMore = summary.RemainingCandidates > 0;
        if (runCanceled)
        {
            summary.Messages.Add("Run canceled; saved tagged bookmarks completed before cancel.");
        }
        telemetryScope.AppendSummaryTo(summary.Messages);
        PopulateProviderTimings(summary, telemetryScope);
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
        var prepared = await PrepareRunCandidatesAsync(
                folderId,
                forceRefresh,
                maxCandidates,
                excludedBookmarkIds,
                summary,
                runState,
                cancellationToken)
            .ConfigureAwait(false);

        if (prepared is null || prepared.IsEmpty)
            return summary;

        var sourceTagCache = new ConcurrentDictionary<SourceTagLookupKey, List<ProvenanceTagEntry>>();
        var providerFailedKeys = new ConcurrentDictionary<SourceTagLookupKey, byte>();

        await ProcessDeterministicPassAsync(
                prepared.DeterministicCandidates,
                sourceTagCache,
                providerFailedKeys,
                summary,
                runState,
                cancellationToken)
            .ConfigureAwait(false);

        if (!summary.StopForRateLimit)
        {
            await ProcessAiPassAsync(
                    prepared.AmbiguousCandidates,
                    sourceTagCache,
                    providerFailedKeys,
                    summary,
                    runState,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (runState.TagsPendingSave > 0)
            runState.TagsPendingSave = await SaveTaggedBookmarksAsync(summary, runState.TagsPendingSave, cancellationToken).ConfigureAwait(false);

        runState.TotalEligibleCount = prepared.TotalEligibleCount;
        summary.RemainingCandidates = runState.TotalEligibleCount - summary.ProcessedBookmarkIds.Count;
        summary.HasMore = summary.RemainingCandidates > 0;
        if (summary.Tagged > 0 && !cancellationToken.IsCancellationRequested)
            summary.Messages.Add($"Finished with {summary.Tagged} tagged bookmark(s) saved to database.");
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

    private static void PopulateProviderTimings(AiAutoTagSummaryDto summary, AutoTagRunTelemetry telemetry)
    {
        var records = telemetry.SnapshotRecords();
        if (records.Count == 0)
            return;

        summary.ProviderTimings = records
            .GroupBy(r => (r.Provider, r.Operation))
            .OrderBy(g => g.Key.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Operation, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var calls = g.ToList();
                var cacheHits = calls.Count(c => c.CacheHit);
                return new ProviderTimingDto
                {
                    Provider = g.Key.Provider,
                    Operation = g.Key.Operation,
                    NetworkCalls = calls.Count - cacheHits,
                    CacheHits = cacheHits,
                    LimiterMs = calls.Sum(c => c.LimiterWaitMs),
                    HttpMs = calls.Sum(c => c.HttpMs)
                };
            })
            .ToList();
    }
}
