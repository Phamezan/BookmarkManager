using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Embedding;

namespace BookmarkManager.Api.Services.Rerank;

/// <summary>Document text fed to the reranker: the same text the embedding model sees
/// (<see cref="LibraryEmbeddingText.Build"/>), truncated to a safe character budget before tokenization so
/// a handful of pathologically long synopses can't blow up per-query latency. The pair encoder still
/// truncates at the token level to fit the model's sequence limit regardless - this is just a cheap
/// pre-trim.</summary>
public static class RerankDocumentText
{
    public static string Build(LibraryCatalogEntry entry)
    {
        var text = LibraryEmbeddingText.Build(entry);
        return text.Length > RerankConstants.RerankPassageMaxChars
            ? text[..RerankConstants.RerankPassageMaxChars]
            : text;
    }
}
