using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Rerank;

/// <summary>Stage-2 reorder step shared by the RAG chat path and the diagnostics endpoint: given the
/// stage-1 hybrid ordering and each candidate's document text, scores every (query, doc) pair through
/// <see cref="IRerankerService"/> and returns the top <c>topK</c> in rerank order. Falls back to the first
/// <c>topK</c> of the hybrid order - unchanged - whenever the reranker isn't ready or a rerank call fails,
/// so a still-downloading or broken model never breaks the assistant.</summary>
public static class RerankPipeline
{
    public sealed record Result(
        IReadOnlyList<Guid> OrderedIds,
        IReadOnlyDictionary<Guid, float> Scores,
        bool Applied);

    public static async Task<Result> ApplyAsync(
        IRerankerService reranker,
        string query,
        IReadOnlyList<Guid> hybridOrderIds,
        IReadOnlyDictionary<Guid, string> textById,
        int topK,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(hybridOrderIds);
        ArgumentNullException.ThrowIfNull(textById);
        ArgumentNullException.ThrowIfNull(logger);

        if (!reranker.IsReady || hybridOrderIds.Count == 0)
            return Fallback(hybridOrderIds, topK);

        var passages = hybridOrderIds.Select(id => textById.GetValueOrDefault(id, string.Empty)).ToList();

        IReadOnlyList<float> scores;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            scores = await reranker.ScoreAsync(query, passages, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Stage-2 rerank failed; falling back to stage-1 hybrid ordering.");
            return Fallback(hybridOrderIds, topK);
        }
        finally
        {
            stopwatch.Stop();
        }

        logger.LogInformation(
            "Stage-2 rerank scored {Count} candidates in {ElapsedMs}ms.", passages.Count, stopwatch.ElapsedMilliseconds);

        if (scores.Count != hybridOrderIds.Count)
        {
            logger.LogWarning(
                "Stage-2 rerank returned {ScoreCount} scores for {CandidateCount} candidates; falling back to stage-1 hybrid ordering.",
                scores.Count, hybridOrderIds.Count);
            return Fallback(hybridOrderIds, topK);
        }

        var scoreById = new Dictionary<Guid, float>(hybridOrderIds.Count);
        for (var i = 0; i < hybridOrderIds.Count; i++)
            scoreById[hybridOrderIds[i]] = scores[i];

        // OrderByDescending is a stable sort, so candidates tied on rerank score keep their relative
        // stage-1 order rather than shuffling arbitrarily.
        var ordered = hybridOrderIds.OrderByDescending(id => scoreById[id]).Take(topK).ToList();
        return new Result(ordered, scoreById, Applied: true);
    }

    private static Result Fallback(IReadOnlyList<Guid> hybridOrderIds, int topK) =>
        new(hybridOrderIds.Take(topK).ToList(), new Dictionary<Guid, float>(), Applied: false);
}
