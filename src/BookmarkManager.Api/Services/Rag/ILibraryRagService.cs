using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Rag;

/// <summary>Answers a Library question grounded on the local catalog: embeds the query, retrieves the
/// nearest catalog entries via <see cref="Vector.IVectorSearchService"/>, and asks an OpenAI-compatible
/// LLM to respond using only that retrieved context.</summary>
public interface ILibraryRagService
{
    Task<LibraryChatResponseDto> ChatAsync(LibraryChatRequestDto request, CancellationToken cancellationToken);
}
