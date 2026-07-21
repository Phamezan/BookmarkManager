using BookmarkManager.Api.Data;
using BookmarkManager.Api.Infrastructure;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

/// <summary>Diagnostic-only endpoint that exposes the auto-tagging title-matching internals
/// (<see cref="MediaTitleNormalizer.Normalize"/> output, catalog match score breakdowns, and
/// threshold verdicts) for debugging "bookmark got no tags" investigations. Not consumed by the
/// Blazor client or the browser extension.</summary>
[ApiController]
[Route("api/tag-explain")]
public class TagExplainController(AppDbContext db, CatalogTaggingService catalogTagging) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TagExplainResponse>> ExplainAsync(
        [FromQuery] string? title,
        [FromQuery] string? url,
        [FromQuery] Guid? bookmarkId,
        [FromQuery] string? domain,
        [FromQuery] string? compareTo,
        [FromQuery] int topN = 10,
        CancellationToken ct = default)
    {
        if (bookmarkId is null && string.IsNullOrWhiteSpace(title))
        {
            return ApiProblem.Result(
                StatusCodes.Status400BadRequest,
                ApiProblem.ValidationCode,
                "Invalid request",
                "Either bookmarkId or title must be provided.");
        }

        string effectiveTitle;
        string? effectiveUrl;

        if (bookmarkId is not null)
        {
            var bookmark = await db.BookmarkNodes
                .AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == bookmarkId.Value, ct)
                .ConfigureAwait(false);
            if (bookmark is null)
            {
                return ApiProblem.Result(
                    StatusCodes.Status404NotFound,
                    "NOT_FOUND",
                    "Bookmark not found",
                    $"No bookmark with id {bookmarkId}.");
            }

            effectiveTitle = string.IsNullOrWhiteSpace(title) ? bookmark.Title : title;
            effectiveUrl = string.IsNullOrWhiteSpace(url) ? bookmark.Url : url;
        }
        else
        {
            effectiveTitle = title!;
            effectiveUrl = url;
        }

        var parsedDomain = Enum.TryParse<BookmarkTagDomain>(domain, ignoreCase: true, out var parsed)
            ? parsed
            : BookmarkTagDomain.Novel;

        var normalizeResult = MediaTitleNormalizer.Normalize(effectiveTitle, effectiveUrl, parsedDomain);

        var normalization = new NormalizationExplain(
            normalizeResult.OriginalTitle,
            normalizeResult.Url,
            normalizeResult.Host,
            normalizeResult.Segments
                .Select(segment => new SegmentExplainDto(
                    segment.Text,
                    segment.Position,
                    segment.Score,
                    segment.Features.IsBrand,
                    segment.Features.IsNoisePhrase,
                    segment.Features.HasChapterMarker,
                    segment.Features.IsPureChapterMarker,
                    segment.Features.LooksLikeTitle,
                    segment.Features.WordCount))
                .ToList(),
            normalizeResult.Candidates
                .Select(candidate => new CandidateExplainDto(candidate.Query, candidate.Confidence, candidate.Reason))
                .ToList());

        var thresholds = new ThresholdsExplain(
            SimilarityThresholds.Default,
            SimilarityThresholds.AniList,
            SimilarityThresholds.Kitsu,
            SimilarityThresholds.MangaUpdates,
            SimilarityThresholds.Catalog,
            SimilarityThresholds.AniListSlug);

        var catalogMatches = new List<CatalogMatchExplainDto>();
        if (parsedDomain == BookmarkTagDomain.Novel)
        {
            var candidates = normalizeResult.Candidates.Take(MediaTitleNormalizer.MaxProviderCandidates).ToList();
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var matches = await catalogTagging.ExplainTopMatchesAsync(candidate.Query, topN, ct).ConfigureAwait(false);
                catalogMatches.AddRange(matches.Select(match => new CatalogMatchExplainDto(
                    i,
                    candidate.Query,
                    match.EntryId,
                    match.Title,
                    match.Score,
                    ToBreakdownDto(match.Breakdown),
                    match.Score >= SimilarityThresholds.Catalog)));
            }
        }

        List<CompareToExplainDto>? compareToExplains = null;
        if (!string.IsNullOrWhiteSpace(compareTo))
        {
            var compareToTokens = MediaTitleNormalizer.NormalizeForSearch(compareTo)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);

            var candidates = normalizeResult.Candidates.Take(MediaTitleNormalizer.MaxProviderCandidates).ToList();
            compareToExplains = [];
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var candidateTokens = MediaTitleNormalizer.NormalizeForSearch(candidate.Query)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.Ordinal);
                var breakdown = MediaTitleNormalizer.ExplainTokenSets(candidateTokens, compareToTokens);
                compareToExplains.Add(new CompareToExplainDto(
                    i,
                    candidate.Query,
                    ToBreakdownDto(breakdown),
                    breakdown.Score >= SimilarityThresholds.Default));
            }
        }

        var response = new TagExplainResponse(normalization, thresholds, catalogMatches, compareToExplains);
        return Ok(response);
    }

    private static TokenSetScoreBreakdownDto ToBreakdownDto(TokenSetScoreBreakdown breakdown)
        => new(
            breakdown.Jaccard,
            breakdown.QueryCoverage,
            breakdown.LengthPenalty,
            breakdown.Score,
            breakdown.SharedTokens,
            breakdown.QueryOnlyTokens,
            breakdown.CandidateOnlyTokens);
}
