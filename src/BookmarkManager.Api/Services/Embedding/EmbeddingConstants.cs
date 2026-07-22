namespace BookmarkManager.Api.Services.Embedding;

/// <summary>Shared constants for the in-process embedding + RAG engine.</summary>
public static class EmbeddingConstants
{
    /// <summary>Output dimensionality of bge-base-en-v1.5.</summary>
    public const int EmbeddingDimensions = 768;

    /// <summary>Identifies the active embedding model. Folded into each row's stored freshness hash so
    /// swapping models invalidates every embedding and forces the backfill worker to re-embed.</summary>
    public const string ModelTag = "bge-base-en-v1.5";

    /// <summary>bge is asymmetric: queries must be prefixed with this instruction, documents are embedded
    /// as-is. Applied only on the query embedding path (RAG chat, semantic search, diagnostics).</summary>
    public const string QueryInstructionPrefix = "Represent this sentence for searching relevant passages: ";

    /// <summary>Number of nearest catalog entries retrieved for a RAG query.</summary>
    public const int RagTopK = 8;

    /// <summary>Minimum cosine similarity a candidate must clear to be retrieved. bge scores are more
    /// spread than MiniLM's, so a higher floor is meaningful.</summary>
    public const float RagMinSimilarity = 0.5f;

    /// <summary>Rows embedded per backfill save cycle.</summary>
    public const int BackfillBatchSize = 64;
}
