namespace BookmarkManager.Api.Services.Embedding;

/// <summary>Shared constants for the in-process embedding + RAG engine.</summary>
public static class EmbeddingConstants
{
    /// <summary>Output dimensionality of all-MiniLM-L6-v2.</summary>
    public const int EmbeddingDimensions = 384;

    /// <summary>Number of nearest catalog entries retrieved for a RAG query.</summary>
    public const int RagTopK = 8;

    /// <summary>Minimum cosine similarity a candidate must clear to be retrieved.</summary>
    public const float RagMinSimilarity = 0.3f;

    /// <summary>Rows embedded per backfill save cycle.</summary>
    public const int BackfillBatchSize = 64;
}
