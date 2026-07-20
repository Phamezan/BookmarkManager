using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services;

public interface IExtensionService
{
    Task<HeartbeatResponse> HandleHeartbeatAsync(HeartbeatRequest request, CancellationToken ct);
    Task<ExtensionConfigDto> GetConfigAsync(CancellationToken ct);
    Task<ExtensionStatusDto> GetStatusAsync(CancellationToken ct);
    Task<ClaimResponse> ClaimCommandsAsync(ClaimRequest request, CancellationToken ct);
    Task<EventBatchResponse> SendEventsAsync(EventBatchRequest request, CancellationToken ct);
    Task<SnapshotResponseDto> UploadSnapshotAsync(SnapshotRequestPayloadDto request, CancellationToken ct);
    Task CompleteCommandAsync(Guid operationId, CompletionRequest request, CancellationToken ct);
    Task ResetDatabaseAsync(CancellationToken ct);
    Task<ExtensionBookmarkEnrichmentDto?> GetBookmarkEnrichmentByBrowserIdAsync(string browserNodeId, CancellationToken ct);

    /// <summary>
    /// Sets the cover image for a synced bookmark identified by its browser node id.
    /// No-ops (returns true) when the node already has a cover, so catalog/library
    /// covers keep precedence. Returns false when the node cannot be found.
    /// </summary>
    Task<bool> SetBookmarkCoverByBrowserIdAsync(string browserNodeId, string? coverImageUrl, CancellationToken ct);
}
