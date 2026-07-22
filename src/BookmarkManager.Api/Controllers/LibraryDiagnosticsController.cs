using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

/// <summary>Read-only RAG coverage diagnostics: reports how much of the catalog is embedded, whether a
/// given title is present/embedded, and how a query ranks against the vector index. Reuses the same
/// <see cref="IEmbeddingService"/> and <see cref="IVectorSearchService"/> the Library assistant uses.</summary>
[ApiController]
[Route("api/library/diagnostics")]
public sealed class LibraryDiagnosticsController(
    IEmbeddingService embeddingService,
    IVectorSearchService vectorSearch,
    AppDbContext db) : ControllerBase
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
                UpToDateCount: upToDateCount));
        }

        IReadOnlyList<LibraryQueryMatchDto>? queryMatches = null;
        LibraryTitleQueryRankDto? titleRank = null;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var queryVector = await embeddingService.EmbedAsync(query.Trim(), cancellationToken).ConfigureAwait(false);
            queryMatches = await RunQueryAsync(queryVector, cancellationToken).ConfigureAwait(false);

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
            UpToDateCount: upToDateCount));
    }

    /// <summary>Counts embedded rows whose stored hash still matches the hash of the current embed text.
    /// Projects only the text columns (never the Embedding blob) so a full-catalog freshness scan stays cheap.</summary>
    private async Task<int> CountUpToDateEmbeddingsAsync(CancellationToken cancellationToken)
    {
        var rows = await db.LibraryCatalogEntries.AsNoTracking()
            .Where(e => e.Embedding != null)
            .Select(e => new { e.Title, e.AlternateTitles, e.Genres, e.Synopsis, e.EmbeddingSourceHash })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var upToDate = 0;
        foreach (var r in rows)
        {
            var text = LibraryEmbeddingText.Build(new LibraryCatalogEntry
            {
                Title = r.Title,
                AlternateTitles = r.AlternateTitles,
                Genres = r.Genres,
                Synopsis = r.Synopsis
            });
            if (string.Equals(r.EmbeddingSourceHash, LibraryEmbeddingText.Hash(text), StringComparison.Ordinal))
                upToDate++;
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
        var currentHash = LibraryEmbeddingText.Hash(LibraryEmbeddingText.Build(entry));
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

    /// <summary>Finds the matched title's score and rank against the query using a wider search (large K,
    /// floor below the retrieval threshold) so it surfaces even when it would not be retrieved normally.</summary>
    private async Task<LibraryTitleQueryRankDto> RankTitleForQueryAsync(
        float[] queryVector, LibraryCatalogEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Embedding is null)
            return new LibraryTitleQueryRankDto(entry.Title, Score: null, Rank: null, AboveFloor: false);

        var wideK = await db.LibraryCatalogEntries.AsNoTracking()
            .CountAsync(e => e.Embedding != null, cancellationToken).ConfigureAwait(false);

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
