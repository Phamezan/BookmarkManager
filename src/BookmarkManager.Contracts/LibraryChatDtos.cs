namespace BookmarkManager.Contracts;

/// <summary>One turn in a Library RAG conversation. <c>Role</c> is <c>"user"</c> or <c>"assistant"</c>
/// (matching the OpenAI-compatible chat convention).</summary>
public sealed record ChatMessageDto(string Role, string Content);

/// <summary>A question posted to the Library AI assistant, optionally with prior conversation turns
/// for follow-up context.</summary>
public sealed record LibraryChatRequestDto
{
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<ChatMessageDto> History { get; init; } = [];
}

/// <summary>A catalog entry the assistant grounded its answer on, surfaced as a clickable card.</summary>
public sealed record LibraryRecommendedSeriesDto(
    string Provider,
    string ProviderId,
    string Title,
    string? CoverImageUrl,
    string? Synopsis,
    IReadOnlyList<string> Genres,
    LibraryMediaType MediaType,
    string SourceUrl,
    float Score);

/// <summary>The assistant's grounded answer: markdown prose plus the series cards it drew from.</summary>
public sealed record LibraryChatResponseDto(
    string Markdown,
    IReadOnlyList<LibraryRecommendedSeriesDto> Series);
