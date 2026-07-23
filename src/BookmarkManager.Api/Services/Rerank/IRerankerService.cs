using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BookmarkManager.Api.Services.Rerank;

/// <summary>In-process stage-2 cross-encoder reranker backed by a local ONNX bge-reranker-base model.
/// Unlike the bi-encoder (<see cref="BookmarkManager.Api.Services.Embedding.IEmbeddingService"/>), which
/// embeds query and document independently, a cross-encoder feeds the query and each candidate document
/// through the transformer together so attention runs across both texts - far more accurate, but too slow
/// to run over the whole catalog, so it only rescores the small candidate pool stage-1 retrieval already
/// narrowed down.</summary>
public interface IRerankerService
{
    /// <summary>True once the ONNX session and tokenizer are loaded and pairs can be scored. False while
    /// the model is still downloading, or permanently if first-boot download failed offline (callers
    /// degrade gracefully to the stage-1 ordering rather than throwing).</summary>
    bool IsReady { get; }

    /// <summary>Scores every (query, passage) pair in one batched ONNX run and returns the raw logits in
    /// input order (same length as <paramref name="passages"/>). Higher is more relevant. These are
    /// independent per-pair scores, not a distribution - never softmax them across candidates. Throws
    /// <see cref="System.InvalidOperationException"/> if the service is not <see cref="IsReady"/>.</summary>
    Task<IReadOnlyList<float>> ScoreAsync(
        string query, IReadOnlyList<string> passages, CancellationToken cancellationToken);
}
