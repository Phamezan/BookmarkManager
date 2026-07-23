using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BookmarkManager.Api.Services.Embedding;

/// <summary>In-process text embedding backed by a local ONNX bge-base-en-v1.5 model. Produces
/// L2-normalized 768-dim vectors suitable for cosine similarity via a plain dot product.</summary>
public interface IEmbeddingService
{
    /// <summary>True once the ONNX session and tokenizer are loaded and embeddings can be produced.
    /// False while the model is still downloading, or permanently if first-boot download failed
    /// offline (callers degrade gracefully rather than throwing).</summary>
    bool IsReady { get; }

    /// <summary>Embeds a single document string (no query prefix). Throws
    /// <see cref="System.InvalidOperationException"/> if the service is not <see cref="IsReady"/>.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken);

    /// <summary>Embeds a search query. bge is asymmetric, so queries are prefixed with a retrieval
    /// instruction while documents are not — always use this for the query side of a search.</summary>
    Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken);

    /// <summary>Embeds a batch of documents in input order. Throws if the service is not <see cref="IsReady"/>.</summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}
