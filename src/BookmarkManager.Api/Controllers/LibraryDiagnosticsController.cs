using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Api.Services.Rerank;
using BookmarkManager.Api.Services.Search;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Controllers;

/// <summary>Read-only RAG coverage diagnostics: reports how much of the catalog is embedded, whether a
/// given title is present/embedded, and how a query ranks against the vector index. Reuses the same
/// <see cref="IEmbeddingService"/> and <see cref="IVectorSearchService"/> the Library assistant uses.
/// The pure-vector "wide probe" (<see cref="RunQueryAsync"/>/<see cref="RankTitleForQueryAsync"/>) stays
/// unchanged - a debugging tool that must report raw cosine rank - with hybrid (RRF-fused) results
/// reported alongside via <see cref="IHybridSearchService"/>, and stage-2 reranked results alongside that
/// via <see cref="IRerankerService"/>/<see cref="RerankPipeline"/>, so the diagnostics pane can compare
/// all three.</summary>
[ApiController]
[Route("api/library/diagnostics")]
public sealed class LibraryDiagnosticsController(
    IEmbeddingService embeddingService,
    IVectorSearchService vectorSearch,
    IHybridSearchService hybridSearch,
    IRerankerService reranker,
    AppDbContext db,
    ILogger<LibraryDiagnosticsController> logger) : ControllerBase
{
    /// <summary>Floor low enough to surface a title even when its cosine similarity is far below the
    /// retrieval threshold (cosine ranges [-1, 1]); used only for the wider title-rank probe.</summary>
    private const float WideSearchFloor = -1f;

    [HttpGet("embedding")]
    public async Task<ActionResult<LibraryEmbeddingDiagnosticDto>> GetEmbeddingDiagnostic(
        [FromQuery] string? title,
        [FromQuery] string? query,
        CancellationToken cancellationToken)
    {
        var totalCount = await db.LibraryCatalogEntries.AsNoTracking()
            .CountAsync(cancellationToken).ConfigureAwait(false);
        var embeddedCount = await db.LibraryCatalogEntries.AsNoTracking()
            .CountAsync(e => e.Embedding != null, cancellationToken).ConfigureAwait(false);
        var embeddedPercent = totalCount == 0
            ? 0d
            : Math.Round(embeddedCount * 100d / totalCount, 1);

        var upToDateCount = await CountUpToDateEmbeddingsAsync(cancellationToken).ConfigureAwait(false);

        // Title lookup needs no model - it is a plain catalog membership + embedded check.
        var titleResult = string.IsNullOrWhiteSpace(title)
            ? null
            : await LookupTitleAsync(title.Trim(), cancellationToken).ConfigureAwait(false);
        var matchedEntry = titleResult?.Entry;

        if (!embeddingService.IsReady)
        {
            return Ok(new LibraryEmbeddingDiagnosticDto(
                ModelReady: false,
                TotalCount: totalCount,
                EmbeddedCount: embeddedCount,
                EmbeddedPercent: embeddedPercent,
                Title: titleResult?.Dto,
                QueryMatches: null,
                TitleRank: null,
                UpToDateCount: upToDateCount,
                HybridMatches: null,
                RerankerReady: reranker.IsReady));
        }

        IReadOnlyList<LibraryQueryMatchDto>? queryMatches = null;
        LibraryTitleQueryRankDto? titleRank = null;
        IReadOnlyList<LibraryHybridMatchDto>? hybridMatches = null;
        IReadOnlyList<LibraryRerankMatchDto>? rerankMatches = null;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var trimmedQuery = query.Trim();
            var queryVector = await embeddingService.EmbedQueryAsync(trimmedQuery, cancellationToken).ConfigureAwait(false);
            queryMatches = await RunQueryAsync(queryVector, cancellationToken).ConfigureAwait(false);
            hybridMatches = await RunHybridQueryAsync(trimmedQuery, queryVector, cancellationToken).ConfigureAwait(false);
            rerankMatches = await RunRerankQueryAsync(trimmedQuery, queryVector, cancellationToken).ConfigureAwait(false);

            if (matchedEntry is not null)
                titleRank = await RankTitleForQueryAsync(queryVector, matchedEntry, cancellationToken).ConfigureAwait(false);
        }

        return Ok(new LibraryEmbeddingDiagnosticDto(
            ModelReady: true,
            TotalCount: totalCount,
            EmbeddedCount: embeddedCount,
            EmbeddedPercent: embeddedPercent,
            Title: titleResult?.Dto,
            QueryMatches: queryMatches,
            TitleRank: titleRank,
            UpToDateCount: upToDateCount,
            HybridMatches: hybridMatches,
            RerankerReady: reranker.IsReady,
            RerankMatches: rerankMatches));
    }

    private static (int Count, DateTimeOffset CachedAt)? _upToDateCache;
    private static readonly object _upToDateLock = new();

    /// <summary>Counts embedded rows whose stored hash still matches the hash of the current embed text.
    /// Uses a short 30-second in-memory cache to avoid re-hashing 50k+ catalog rows on every UI click.</summary>
    private async Task<int> CountUpToDateEmbeddingsAsync(CancellationToken cancellationToken)
    {
        lock (_upToDateLock)
        {
            if (_upToDateCache is { } cached && DateTimeOffset.UtcNow - cached.CachedAt < TimeSpan.FromSeconds(30))
                return cached.Count;
        }

        var rows = await db.LibraryCatalogEntries.AsNoTracking()
            .Where(e => e.Embedding != null)
            .Select(e => new { e.Title, e.AlternateTitles, e.Genres, e.Synopsis, e.EmbeddingSourceHash })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var upToDate = 0;
        foreach (var r in rows)
        {
            var currentHash = LibraryEmbeddingText.SourceHash(new LibraryCatalogEntry
            {
                Title = r.Title,
                AlternateTitles = r.AlternateTitles,
                Genres = r.Genres,
                Synopsis = r.Synopsis
            });
            if (string.Equals(r.EmbeddingSourceHash, currentHash, StringComparison.Ordinal))
                upToDate++;
        }

        lock (_upToDateLock)
        {
            _upToDateCache = (upToDate, DateTimeOffset.UtcNow);
        }

        return upToDate;
    }

    private sealed record TitleLookup(LibraryTitleEmbeddingDto Dto, LibraryCatalogEntry? Entry);

    private async Task<TitleLookup> LookupTitleAsync(string title, CancellationToken cancellationToken)
    {
        var lower = title.ToLower();
        var entry = await db.LibraryCatalogEntries.AsNoTracking()
            .Where(e => e.Title.ToLower().Contains(lower)
                || (e.AlternateTitles != null && e.AlternateTitles.ToLower().Contains(lower)))
            .OrderBy(e => e.Title.Length) // closest (shortest superstring) match first
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
            return new TitleLookup(new LibraryTitleEmbeddingDto(title, false, null, null, false, null), null);

        // Stale = embedded, but the stored hash no longer matches the hash of the current embed text
        // (Title + AlternateTitles + Genres + Synopsis). Such rows were embedded from older/thinner text
        // and rank poorly until the backfill worker re-embeds them.
        var embedded = entry.Embedding is not null;
        var currentHash = LibraryEmbeddingText.SourceHash(entry);
        var stale = embedded && !string.Equals(entry.EmbeddingSourceHash, currentHash, StringComparison.Ordinal);

        var dto = new LibraryTitleEmbeddingDto(
            title,
            Found: true,
            MatchedTitle: entry.Title,
            MediaType: entry.MediaType,
            Embedded: embedded,
            EmbeddingSourceHash: entry.EmbeddingSourceHash,
            HasSynopsis: !string.IsNullOrWhiteSpace(entry.Synopsis),
            EmbeddingStale: stale);
        return new TitleLookup(dto, entry);
    }

    private async Task<IReadOnlyList<LibraryQueryMatchDto>> RunQueryAsync(
        float[] queryVector, CancellationToken cancellationToken)
    {
        var hits = await vectorSearch
            .SearchAsync(queryVector, EmbeddingConstants.RagTopK, EmbeddingConstants.RagMinSimilarity, cancellationToken)
            .ConfigureAwait(false);
        if (hits.Count == 0)
            return Array.Empty<LibraryQueryMatchDto>();

        var ids = hits.Select(h => h.Id).ToList();
        var entriesById = await db.LibraryCatalogEntries.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);

        var matches = new List<LibraryQueryMatchDto>(hits.Count);
        foreach (var (id, score) in hits)
        {
            if (entriesById.TryGetValue(id, out var entry))
                matches.Add(new LibraryQueryMatchDto(entry.Title, entry.MediaType, score));
        }

        return matches;
    }

    /// <summary>Same query, run through the hybrid (dense + FTS5/BM25, RRF-fused) path so the
    /// diagnostics pane can show it next to the pure-vector <see cref="RunQueryAsync"/> results.</summary>
    private async Task<IReadOnlyList<LibraryHybridMatchDto>> RunHybridQueryAsync(
        string query, float[] queryVector, CancellationToken cancellationToken)
    {
        var hits = await hybridSearch
            .SearchAsync(query, queryVector, EmbeddingConstants.RagTopK, cancellationToken)
            .ConfigureAwait(false);
        if (hits.Count == 0)
            return Array.Empty<LibraryHybridMatchDto>();

        var ids = hits.Select(h => h.Id).ToList();
        var entriesById = await db.LibraryCatalogEntries.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);

        var matches = new List<LibraryHybridMatchDto>(hits.Count);
        foreach (var (id, score, rrfScore) in hits)
        {
            if (entriesById.TryGetValue(id, out var entry))
                matches.Add(new LibraryHybridMatchDto(entry.Title, entry.MediaType, score, rrfScore));
        }

        return matches;
    }

    /// <summary>Same query, stage-1 hybrid pool widened to <see cref="RerankConstants.RerankCandidatePool"/>
    /// and reordered through <see cref="RerankPipeline"/> so the diagnostics pane can see the stage-2
    /// effect. Returns an empty (not null) list when the reranker degraded and fell back to hybrid order
    /// unchanged, so the UI can distinguish "reranker ran, nothing left after RagTopK" from "not ready" -
    /// the latter is already reported via <c>RerankerReady</c>.</summary>
    private async Task<IReadOnlyList<LibraryRerankMatchDto>?> RunRerankQueryAsync(
        string query, float[] queryVector, CancellationToken cancellationToken)
    {
        if (!reranker.IsReady)
            return null;

        var hits = await hybridSearch
            .SearchAsync(query, queryVector, RerankConstants.RerankCandidatePool, cancellationToken)
            .ConfigureAwait(false);
        if (hits.Count == 0)
            return Array.Empty<LibraryRerankMatchDto>();

        var hybridOrderIds = hits.Select(h => h.Id).ToList();
        var hybridRankById = hybridOrderIds
            .Select((id, index) => (id, rank: index + 1))
            .ToDictionary(x => x.id, x => x.rank);

        var entriesById = await db.LibraryCatalogEntries.AsNoTracking()
            .Where(e => hybridOrderIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);

        var textById = entriesById.ToDictionary(kv => kv.Key, kv => RerankDocumentText.Build(kv.Value));
        var result = await RerankPipeline
            .ApplyAsync(reranker, query, hybridOrderIds, textById, EmbeddingConstants.RagTopK, logger, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Applied)
            return Array.Empty<LibraryRerankMatchDto>();

        var matches = new List<LibraryRerankMatchDto>(result.OrderedIds.Count);
        foreach (var id in result.OrderedIds)
        {
            if (!entriesById.TryGetValue(id, out var entry))
                continue;

            var logit = result.Scores.GetValueOrDefault(id);
            matches.Add(new LibraryRerankMatchDto(
                entry.Title, entry.MediaType, logit, RerankScoring.Sigmoid(logit), hybridRankById.GetValueOrDefault(id)));
        }
        return matches;
    }

    /// <summary>Finds the matched title's score and rank against the query using a wider search (large K,
    /// floor below the retrieval threshold) so it surfaces even when it would not be retrieved normally.</summary>
    private async Task<LibraryTitleQueryRankDto> RankTitleForQueryAsync(
        float[] queryVector, LibraryCatalogEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Embedding is null)
            return new LibraryTitleQueryRankDto(entry.Title, Score: null, Rank: null, AboveFloor: false);

        var totalEmbedded = await db.LibraryCatalogEntries.AsNoTracking()
            .CountAsync(e => e.Embedding != null, cancellationToken).ConfigureAwait(false);
        var wideK = Math.Min(2000, totalEmbedded);

        var ranked = await vectorSearch
            .SearchAsync(queryVector, wideK, WideSearchFloor, cancellationToken)
            .ConfigureAwait(false);

        for (var i = 0; i < ranked.Count; i++)
        {
            if (ranked[i].Id != entry.Id)
                continue;

            var score = ranked[i].Score;
            return new LibraryTitleQueryRankDto(
                entry.Title,
                Score: score,
                Rank: i + 1,
                AboveFloor: score >= EmbeddingConstants.RagMinSimilarity);
        }

        return new LibraryTitleQueryRankDto(entry.Title, Score: null, Rank: null, AboveFloor: false);
    }
}
