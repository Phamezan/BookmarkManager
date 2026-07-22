using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services.Rerank;

namespace BookmarkManager.UnitTests.Rerank;

/// <summary>Test double for <see cref="IRerankerService"/>. <see cref="ScoreFunc"/> defaults to scoring
/// every passage by its position in the input list, descending (first passage highest), which is enough
/// to prove a caller actually reorders by rerank score rather than passing hybrid order through.</summary>
public sealed class FakeRerankerService : IRerankerService
{
    public bool IsReady { get; set; } = true;

    public Func<string, IReadOnlyList<string>, IReadOnlyList<float>>? ScoreFunc { get; set; }

    public Exception? ThrowOnScore { get; set; }

    public Task<IReadOnlyList<float>> ScoreAsync(
        string query, IReadOnlyList<string> passages, CancellationToken cancellationToken)
    {
        if (ThrowOnScore is not null)
            throw ThrowOnScore;

        if (!IsReady)
            throw new InvalidOperationException("Reranker model is not ready.");

        var scores = ScoreFunc?.Invoke(query, passages)
            ?? DescendingByPosition(passages);
        return Task.FromResult(scores);
    }

    private static IReadOnlyList<float> DescendingByPosition(IReadOnlyList<string> passages)
    {
        var scores = new float[passages.Count];
        for (var i = 0; i < passages.Count; i++)
            scores[i] = passages.Count - i;
        return scores;
    }
}
