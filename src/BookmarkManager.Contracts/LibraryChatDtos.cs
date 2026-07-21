namespace BookmarkManager.Contracts;

/// <summary>One turn in a Library RAG chat exchange. <see cref="Role"/> is <c>"user"</c> or
/// <c>"assistant"</c> (matching the OpenAI-compatible chat convention).</summary>
public sealed record ChatMessageDto(string Role, string Content);

/// <summary>Client request for the Library AI assistant. Carries the new user message plus the
/// prior turns so the server can build a grounded, context-aware prompt.</summary>
public sealed record LibraryChatRequestDto
{
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<ChatMessageDto> History { get; init; } = [];
}

/// <summary>Server reply for the Library AI assistant: a markdown answer plus any catalog series
/// the assistant grounded its answer on, rendered as clickable cards.</summary>
public sealed record LibraryChatResponseDto
{
    public string Reply { get; init; } = string.Empty;
    public IReadOnlyList<LibraryChatSeriesCardDto> Recommendations { get; init; } = [];
}

/// <summary>A compact catalog-series card surfaced alongside a chat reply.</summary>
public sealed record LibraryChatSeriesCardDto(
    string Provider,
    string ProviderId,
    string Title,
    string? CoverImageUrl,
    string? Synopsis,
    double? Rating,
    string SourceUrl);
