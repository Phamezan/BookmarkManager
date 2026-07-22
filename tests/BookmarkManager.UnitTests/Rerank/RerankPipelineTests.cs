using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services.Rerank;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.Rerank;

public sealed class RerankPipelineTests
{
    private static readonly Guid IdA = Guid.NewGuid();
    private static readonly Guid IdB = Guid.NewGuid();
    private static readonly Guid IdC = Guid.NewGuid();

    private static IReadOnlyList<Guid> HybridOrder => new[] { IdA, IdB, IdC };

    private static IReadOnlyDictionary<Guid, string> TextById => new Dictionary<Guid, string>
    {
        [IdA] = "passage a",
        [IdB] = "passage b",
        [IdC] = "passage c"
    };

    [Fact]
    public async Task ApplyAsync_WhenRerankerNotReady_FallsBackToHybridOrderUnchanged()
    {
        var reranker = new FakeRerankerService { IsReady = false };

        var result = await RerankPipeline.ApplyAsync(
            reranker, "query", HybridOrder, TextById, topK: 2, NullLogger.Instance, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(new[] { IdA, IdB }, result.OrderedIds);
        Assert.Empty(result.Scores);
    }

    [Fact]
    public async Task ApplyAsync_WhenReady_ReordersByDescendingRerankScore()
    {
        // IdC scores highest even though it's last in hybrid order - proves the pipeline actually
        // reorders by rerank score rather than passing hybrid order through.
        var reranker = new FakeRerankerService
        {
            IsReady = true,
            ScoreFunc = (_, passages) => new float[] { 0.1f, 0.2f, 0.9f }
        };

        var result = await RerankPipeline.ApplyAsync(
            reranker, "query", HybridOrder, TextById, topK: 3, NullLogger.Instance, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(new[] { IdC, IdB, IdA }, result.OrderedIds);
        Assert.Equal(0.9f, result.Scores[IdC]);
    }

    [Fact]
    public async Task ApplyAsync_RespectsTopKAfterReordering()
    {
        var reranker = new FakeRerankerService
        {
            IsReady = true,
            ScoreFunc = (_, passages) => new float[] { 0.1f, 0.9f, 0.5f }
        };

        var result = await RerankPipeline.ApplyAsync(
            reranker, "query", HybridOrder, TextById, topK: 2, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(new[] { IdB, IdC }, result.OrderedIds);
    }

    [Fact]
    public async Task ApplyAsync_WhenScoreAsyncThrows_FallsBackToHybridOrderUnchanged()
    {
        var reranker = new FakeRerankerService
        {
            IsReady = true,
            ThrowOnScore = new InvalidOperationException("ONNX run failed")
        };

        var result = await RerankPipeline.ApplyAsync(
            reranker, "query", HybridOrder, TextById, topK: 3, NullLogger.Instance, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(new[] { IdA, IdB, IdC }, result.OrderedIds);
    }

    [Fact]
    public async Task ApplyAsync_WhenScoreCountMismatchesCandidateCount_FallsBackInsteadOfThrowing()
    {
        // Reranker returns fewer scores than passages - indexing that directly would throw
        // IndexOutOfRangeException straight out of the RAG chat path. Must degrade instead.
        var reranker = new FakeRerankerService
        {
            IsReady = true,
            ScoreFunc = (_, passages) => new float[] { 0.5f } // only 1 score for 3 candidates
        };

        var result = await RerankPipeline.ApplyAsync(
            reranker, "query", HybridOrder, TextById, topK: 3, NullLogger.Instance, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(new[] { IdA, IdB, IdC }, result.OrderedIds);
        Assert.Empty(result.Scores);
    }

    [Fact]
    public async Task ApplyAsync_WithEmptyHybridOrder_ReturnsEmptyWithoutCallingReranker()
    {
        var reranker = new FakeRerankerService { IsReady = true, ThrowOnScore = new Exception("should not be called") };

        var result = await RerankPipeline.ApplyAsync(
            reranker, "query", Array.Empty<Guid>(), TextById, topK: 3, NullLogger.Instance, CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Empty(result.OrderedIds);
    }
}
