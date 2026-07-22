namespace BookmarkManager.Api.Services.Rerank;

/// <summary>Shared constants for the stage-2 cross-encoder reranker. Kept separate from
/// <see cref="BookmarkManager.Api.Services.Embedding.EmbeddingConstants"/> since these govern a different
/// model (bge-reranker-base) with its own lifecycle and download.</summary>
public static class RerankConstants
{
    /// <summary>Identifies the active reranker model; folds into the models/&lt;tag&gt; download directory,
    /// mirroring <see cref="BookmarkManager.Api.Services.Embedding.EmbeddingConstants.ModelTag"/>.</summary>
    public const string RerankerModelTag = "bge-reranker-base";

    public const string ModelFileName = "model_quantized.onnx";
    public const string ModelFallbackFileName = "model.onnx";
    public const string TokenizerFileName = "tokenizer.json";

    /// <summary>Preferred artifact: int8-quantized, several times smaller and faster on CPU than fp32.
    /// This model runs synchronously per query (not batch-offline like embeddings), so latency matters
    /// more than the small accuracy loss from quantization.</summary>
    public const string ModelUrl =
        "https://huggingface.co/Xenova/bge-reranker-base/resolve/main/onnx/model_quantized.onnx";

    /// <summary>Used only if the quantized artifact 404s on the mirror.</summary>
    public const string ModelFallbackUrl =
        "https://huggingface.co/Xenova/bge-reranker-base/resolve/main/onnx/model.onnx";

    public const string TokenizerUrl =
        "https://huggingface.co/Xenova/bge-reranker-base/resolve/main/tokenizer.json";

    /// <summary>bge-reranker-base (XLM-RoBERTa) supports up to 512 tokens; longer pairs are truncated.</summary>
    public const int MaxSequenceLength = 512;

    /// <summary>Stage-1 (hybrid) candidate pool size handed to stage-2 for rescoring. Wider than
    /// <see cref="BookmarkManager.Api.Services.Embedding.EmbeddingConstants.RagTopK"/> so the reranker has
    /// real material to reorder - re-sorting only 8 candidates can't recover a doc stage-1 fusion buried
    /// at rank 20.</summary>
    public const int RerankCandidatePool = 30;

    /// <summary>Character cap applied to document text before tokenization - a cheap pre-trim so a handful
    /// of pathologically long synopses can't blow up per-query latency. The pair encoder still truncates at
    /// the token level to fit <see cref="MaxSequenceLength"/> regardless.</summary>
    public const int RerankPassageMaxChars = 4000;
}
