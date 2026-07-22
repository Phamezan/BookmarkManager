using System.Numerics.Tensors;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Library;

/// <summary>Fans a query out to every requested (or all enabled) provider concurrently, isolates
/// each provider behind its own hard timeout so one slow scraper can't bottleneck the response,
/// and merges/dedupes the combined results by normalized title + media type. The "Browse" (trending)
/// view instead pages through <see cref="LibraryCatalogEntry"/> - a local mirror kept fresh by
/// <see cref="LibraryCatalogSyncBackgroundService"/> - so it can offer thousands of titles without a
/// live fan-out call on every page load; live search additionally folds in local catalog matches for
/// better recall alongside the freshest live results.
///
/// Deliberately NOT switched to <see cref="Search.IHybridSearchService"/> (feature/hybrid-search): unlike
/// <c>LibraryRagService</c>, this service never does a full-corpus dense search in the first place - the
/// SQL <c>LIKE</c> candidate generation in <see cref="SearchCatalogAsync"/> already guarantees an exact
/// proper-noun substring match surfaces (the failure mode hybrid retrieval exists to fix), and
/// <see cref="RankHybrid"/>/<see cref="HybridScore"/> already blend that keyword signal with cosine
/// similarity over just those LIKE-filtered candidates. Routing this through RRF fusion over the whole
/// catalog would be a bigger behavior change (different candidate set, different scoring) for a path that
/// doesn't have the problem the task was written to solve.</summary>
public sealed class LibrarySearchService(
    LibraryProviderRegistry registry,
    IOptions<LibraryProviderOptions> options,
    AppDbContext db,
    IEmbeddingService embeddingService,
    ILogger<LibrarySearchService> logger)
{
    private const int CatalogSearchMatchLimit = 30;
    private const int MinTakeSize = 1;
    private const int MaxTakeSize = 200;

    // Hybrid ranking weights: keyword relevance still dominates so exact/prefix title hits stay on top,
    // with semantic similarity breaking ties and lifting conceptually-close matches. Sum to 1.0.
    private const double KeywordWeight = 0.6;
    private const double VectorWeight = 0.4;

    /// <summary>Minimum cosine similarity for a semantic match to contribute; below this the vector
    /// component is treated as noise (0) so weak matches don't perturb keyword order. Mirrors
    /// <c>RagMinSimilarity</c> in the vector engine spec.</summary>
    private const float VectorSimilarityFloor = 0.3f;

    private sealed record ProviderOutcome(IReadOnlyList<LibraryEntryDto> Entries, LibraryProviderStatusDto Status);

    public async Task<LibrarySearchResponse> SearchAsync(LibrarySearchRequest request, CancellationToken cancellationToken)
    {
        var response = new LibrarySearchResponse();
        var query = (request.Query ?? string.Empty).Trim();
        if (query.Length < 2)
            return response;

        var (toQuery, precomputedStatuses) = SelectProviders(request.Providers);
        response.ProviderStatuses.AddRange(precomputedStatuses);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.SearchTimeoutSeconds));
        var outcomes = await Task.WhenAll(
            toQuery.Select(p => RunAsync(p, timeout, ct => p.SearchAsync(query, request.MediaType, ct), cancellationToken))
        ).ConfigureAwait(false);

        response.ProviderStatuses.AddRange(outcomes.Select(o => o.Status));

        var catalogMatches = await SearchCatalogAsync(query, request.MediaType, cancellationToken).ConfigureAwait(false);

        var entries = outcomes.SelectMany(o => o.Entries).Concat(catalogMatches);
        if (request.MediaType is { } requestedType)
            entries = entries.Where(e => e.MediaType == requestedType);

        response.Items.AddRange(MergeAndDedupe(entries));
        return response;
    }

    private async Task<List<LibraryEntryDto>> SearchCatalogAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken)
    {
        var pattern = $"%{query}%";
        var rows = await db.LibraryCatalogEntries
            .AsNoTracking()
            .Where(e => (mediaType == null || e.MediaType == mediaType) &&
                        (EF.Functions.Like(e.Title, pattern) ||
                         (e.AlternateTitles != null && EF.Functions.Like(e.AlternateTitles, pattern))))
            .Take(CatalogSearchMatchLimit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return [];

        var ranked = await RankCatalogAsync(rows, query, cancellationToken).ConfigureAwait(false);
        return ranked.Select(MapCatalogEntry).ToList();
    }

    /// <summary>Orders keyword candidates by a hybrid of keyword relevance and semantic (embedding)
    /// similarity. When the embedding service isn't ready - or embedding the query fails - falls back
    /// to the original keyword-only order so search never regresses below the pure-keyword baseline.</summary>
    private async Task<IReadOnlyList<LibraryCatalogEntry>> RankCatalogAsync(
        IReadOnlyList<LibraryCatalogEntry> rows, string query, CancellationToken cancellationToken)
    {
        if (!embeddingService.IsReady)
            return rows;

        float[] queryVector;
        try
        {
            queryVector = await embeddingService.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query embedding failed; falling back to keyword-only catalog ranking.");
            return rows;
        }

        return RankHybrid(rows, query, queryVector);
    }

    /// <summary>Pure, deterministic hybrid ordering - extracted so it can be unit-tested without a DB
    /// or a real ONNX session. Higher <see cref="HybridScore"/> sorts first; ties keep input order.</summary>
    internal static IReadOnlyList<LibraryCatalogEntry> RankHybrid(
        IReadOnlyList<LibraryCatalogEntry> rows, string query, float[] queryVector) =>
        rows.OrderByDescending(row => HybridScore(row, query, queryVector)).ToList();

    internal static double HybridScore(LibraryCatalogEntry row, string query, float[] queryVector)
    {
        var keyword = KeywordScore(row.Title, row.AlternateTitles, query);

        var vector = row.GetEmbeddingVector();
        if (vector is null || vector.Length != queryVector.Length)
            return keyword; // no embedding yet - keyword score only

        var cosine = TensorPrimitives.CosineSimilarity(queryVector, vector);
        var semantic = cosine < VectorSimilarityFloor ? 0.0 : cosine;
        return (KeywordWeight * keyword) + (VectorWeight * semantic);
    }

    /// <summary>Cheap lexical relevance in [0,1]: exact &gt; prefix &gt; substring title match, then
    /// alternate-title substring, then a small floor for LIKE candidates that matched some other way.</summary>
    internal static double KeywordScore(string? title, string? alternateTitles, string query)
    {
        var q = query.Trim();
        if (q.Length == 0)
            return 0.0;

        var t = title ?? string.Empty;
        if (t.Equals(q, StringComparison.OrdinalIgnoreCase))
            return 1.0;
        if (t.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            return 0.8;
        if (t.Contains(q, StringComparison.OrdinalIgnoreCase))
            return 0.6;
        if (alternateTitles is not null && alternateTitles.Contains(q, StringComparison.OrdinalIgnoreCase))
            return 0.4;
        return 0.2;
    }

    /// <summary>Pages the local catalog mirror ordered by popularity rank (nulls last, i.e. coverage-only
    /// entries sort after ranked ones), falling back to a small live fan-out fetch only while the catalog
    /// is still empty (e.g. the background crawl hasn't completed its first pass yet).</summary>
    public async Task<LibrarySearchResponse> GetTrendingAsync(LibraryMediaType? mediaType, int skip, int take, CancellationToken cancellationToken)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, MinTakeSize, MaxTakeSize);

        var query = db.LibraryCatalogEntries.AsNoTracking().AsQueryable();
        if (mediaType is { } type)
            query = query.Where(e => e.MediaType == type);

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        if (totalCount == 0 && skip == 0)
            return await GetLiveTrendingFallbackAsync(mediaType, cancellationToken).ConfigureAwait(false);

        var rows = await query
            .OrderBy(e => e.PopularityRank ?? int.MaxValue)
            .ThenByDescending(e => e.Rating ?? 0)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new LibrarySearchResponse
        {
            Items = rows.Select(MapCatalogEntry).ToList(),
            TotalCount = totalCount,
            HasMore = skip + rows.Count < totalCount
        };
    }

    /// <summary>On-demand, single-title enrichment for catalog rows the bulk crawl only had thin
    /// listing-page data for (no synopsis/genres/author - see <see cref="RanobeDbLibraryProvider"/> and
    /// <see cref="NovelfireLibraryProvider"/>, whose list endpoints don't carry those fields). Called when
    /// the client opens the details popup for an entry that's still missing them; the fetched detail page
    /// is merged back into <see cref="LibraryCatalogEntry"/> so every later view of the same title -
    /// anyone's, not just this caller's - is enriched for free, spreading the extra request cost only
    /// across titles someone actually looked at rather than the entire catalog upfront.</summary>
    public async Task<LibraryEntryDto?> EnrichEntryAsync(string provider, string providerId, CancellationToken cancellationToken)
    {
        var mediaProvider = registry.FindByName(provider);
        if (mediaProvider is null || !mediaProvider.IsEnabled)
            return null;

        LibraryEntryDto? dto;
        try
        {
            dto = await mediaProvider.GetDetailsAsync(providerId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Provider} enrichment fetch failed for {ProviderId}.", provider, providerId);
            return null;
        }

        if (dto is null)
            return null;

        var row = await db.LibraryCatalogEntries
            .FirstOrDefaultAsync(e => e.Provider == provider && e.ProviderId == providerId, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
            return dto;

        LibraryCatalogSyncBackgroundService.ApplyDto(row, dto);
        row.LastRefreshedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return MapCatalogEntry(row);
    }

    /// <summary>Looks up full catalog cards for a specific set of (provider, providerId) keys -
    /// local-DB only, no live provider calls - so the client can render cards for series that
    /// aren't on the currently loaded trending/search page (e.g. the "My bookmarks" filter).</summary>
    public async Task<List<LibraryEntryDto>> GetEntriesByKeysAsync(
        IReadOnlyCollection<(string Provider, string ProviderId)> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
            return [];

        var providers = keys.Select(k => k.Provider).Distinct().ToList();
        var providerIds = keys.Select(k => k.ProviderId).Distinct().ToList();
        var rows = await db.LibraryCatalogEntries
            .Where(e => providers.Contains(e.Provider) && providerIds.Contains(e.ProviderId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var keySet = keys.ToHashSet();
        return rows.Where(r => keySet.Contains((r.Provider, r.ProviderId))).Select(MapCatalogEntry).ToList();
    }

    private static LibraryEntryDto MapCatalogEntry(LibraryCatalogEntry row) => new(
        row.Provider,
        row.ProviderId,
        row.Title,
        LibraryCatalogEntry.SplitList(row.AlternateTitles),
        LibraryCatalogEntry.SplitList(row.Authors),
        row.MediaType,
        row.CoverImageUrl,
        row.Synopsis,
        LibraryCatalogEntry.SplitList(row.Genres),
        row.Rating,
        row.Status,
        row.LatestChapter,
        row.LatestVolume,
        row.LastReleaseAt,
        row.SourceUrl);

    /// <summary>Bootstrap-window fallback used only before the background catalog crawl has imported
    /// anything - preserves the original small live fan-out behavior so the page isn't empty.</summary>
    private async Task<LibrarySearchResponse> GetLiveTrendingFallbackAsync(LibraryMediaType? mediaType, CancellationToken cancellationToken)
    {
        var response = new LibrarySearchResponse();
        var trendingProviders = registry.EnabledProviders.OfType<ITrendingMediaProvider>().ToList();
        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.SearchTimeoutSeconds) * 2);

        var outcomes = await Task.WhenAll(
            trendingProviders.Select(p => RunAsync(
                (IMediaProvider)p,
                timeout,
                ct => p.GetTrendingAsync(mediaType, ct),
                cancellationToken))
        ).ConfigureAwait(false);

        response.ProviderStatuses.AddRange(outcomes.Select(o => o.Status));

        var entries = outcomes.SelectMany(o => o.Entries);
        if (mediaType is { } requestedType)
            entries = entries.Where(e => e.MediaType == requestedType);

        var merged = MergeAndDedupe(entries);
        response.Items.AddRange(merged);
        response.TotalCount = merged.Count;
        response.HasMore = false;
        return response;
    }

    private (List<IMediaProvider> ToQuery, List<LibraryProviderStatusDto> PrecomputedStatuses) SelectProviders(List<string>? requestedNames)
    {
        if (requestedNames is not { Count: > 0 })
            return (registry.EnabledProviders.ToList(), []);

        var toQuery = new List<IMediaProvider>();
        var statuses = new List<LibraryProviderStatusDto>();
        foreach (var name in requestedNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var provider = registry.FindByName(name);
            if (provider is null)
                statuses.Add(new LibraryProviderStatusDto(name, LibraryProviderResultStatus.Failed, "Unknown provider."));
            else if (!provider.IsEnabled)
                statuses.Add(new LibraryProviderStatusDto(provider.ProviderName, LibraryProviderResultStatus.Disabled, null));
            else
                toQuery.Add(provider);
        }

        return (toQuery, statuses);
    }

    private async Task<ProviderOutcome> RunAsync(
        IMediaProvider provider,
        TimeSpan timeout,
        Func<CancellationToken, Task<IReadOnlyList<LibraryEntryDto>>> call,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var entries = await call(linked.Token).ConfigureAwait(false);
            return new ProviderOutcome(entries, new LibraryProviderStatusDto(provider.ProviderName, LibraryProviderResultStatus.Ok, null));
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            logger.LogWarning("{Provider} fan-out call timed out after {TimeoutSeconds}s.", provider.ProviderName, timeout.TotalSeconds);
            return new ProviderOutcome([], new LibraryProviderStatusDto(provider.ProviderName, LibraryProviderResultStatus.Timeout, "Timed out."));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Provider} fan-out call failed.", provider.ProviderName);
            return new ProviderOutcome([], new LibraryProviderStatusDto(provider.ProviderName, LibraryProviderResultStatus.Failed, ex.Message));
        }
    }

    public static List<LibraryEntryDto> MergeAndDedupe(IEnumerable<LibraryEntryDto> entries)
    {
        return entries
            .GroupBy(e => (Title: MediaTitleNormalizer.NormalizeForSearch(e.Title), e.MediaType))
            .Select(group => MergeGroup(group.ToList()))
            .ToList();
    }

    private static LibraryEntryDto MergeGroup(List<LibraryEntryDto> duplicates)
    {
        if (duplicates.Count == 1)
            return duplicates[0];

        var primary = duplicates.OrderByDescending(RichnessScore).First();

        return primary with
        {
            CoverImageUrl = primary.CoverImageUrl ?? duplicates.Select(d => d.CoverImageUrl).FirstOrDefault(c => c is not null),
            Synopsis = primary.Synopsis ?? duplicates.Select(d => d.Synopsis).FirstOrDefault(s => s is not null),
            Rating = primary.Rating ?? duplicates.Select(d => d.Rating).FirstOrDefault(r => r is not null),
            LatestChapter = primary.LatestChapter ?? duplicates.Select(d => d.LatestChapter).FirstOrDefault(c => c is not null),
            LastReleaseAt = primary.LastReleaseAt ?? duplicates.Select(d => d.LastReleaseAt).FirstOrDefault(d => d is not null),
            Genres = primary.Genres.Count > 0
                ? primary.Genres
                : duplicates.SelectMany(d => d.Genres).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Authors = primary.Authors.Count > 0
                ? primary.Authors
                : duplicates.SelectMany(d => d.Authors).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static int RichnessScore(LibraryEntryDto entry) =>
        (entry.CoverImageUrl is not null ? 1 : 0) +
        (entry.Synopsis is not null ? 1 : 0) +
        (entry.Genres.Count > 0 ? 1 : 0) +
        (entry.Rating is not null ? 1 : 0) +
        (entry.LatestChapter is not null ? 1 : 0);
}
