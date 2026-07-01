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
}
