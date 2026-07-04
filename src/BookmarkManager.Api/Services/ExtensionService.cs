using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services;

public sealed partial class ExtensionService(AppDbContext db, BookmarkTaggingService bookmarkTagging) : IExtensionService
{
    public async Task<HeartbeatResponse> HandleHeartbeatAsync(HeartbeatRequest request, CancellationToken ct)
    {
        var client = await GetOrCreateDefaultClientAsync(ct);
        var now = DateTime.UtcNow;

        client.LastHeartbeatAt = now;
        client.ExtensionVersion = request.ExtensionVersion;
        client.BraveVersion = request.BraveVersion;
        client.LocalConfigVersion = request.LocalConfigVersion;
        client.PendingEventCount = request.PendingEventCount;
        client.LastSuccessfulSyncAt = request.LastSuccessfulSyncAt;

        var config = await GetOrCreateAppConfigAsync(ct);

        await db.SaveChangesAsync(ct);

        return new HeartbeatResponse
        {
            ExtensionClientId = client.Id,
            ServerTime = now,
            ConfigVersion = config.ConfigVersion,
            PollIntervalSeconds = config.PollIntervalSeconds
        };
    }

    // DESIGN CONSTRAINT NOTE:
    // This assumes exactly one extension client profile exists forever (V1 assumption).
    // If support for multiple concurrent Brave profiles is added in the future,
    // this single-client logic must be refactored and a database migration applied
    // to map events and heartbeats to specific extension client IDs.
    private async Task<ExtensionClient> GetOrCreateDefaultClientAsync(CancellationToken ct)
    {
        var client = await db.ExtensionClients.FirstOrDefaultAsync(ct);
        if (client is not null)
        {
            return client;
        }

        var now = DateTime.UtcNow;
        client = new ExtensionClient
        {
            Id = Guid.NewGuid(),
            FirstSeenAt = now,
            LastHeartbeatAt = now
        };
        db.ExtensionClients.Add(client);
        await db.SaveChangesAsync(ct);
        return client;
    }


    public async Task<ExtensionStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var client = await db.ExtensionClients.FirstOrDefaultAsync(ct);
        if (client is null)
        {
            return new ExtensionStatusDto { IsConnected = false };
        }

        var config = await GetOrCreateAppConfigAsync(ct);
        var graceMs = config.PollIntervalSeconds * 1000 * 3;
        var isConnected = DateTime.UtcNow - client.LastHeartbeatAt <= TimeSpan.FromMilliseconds(graceMs);

        return new ExtensionStatusDto
        {
            IsConnected = isConnected,
            LastHeartbeatAt = client.LastHeartbeatAt,
            ExtensionVersion = client.ExtensionVersion,
            BraveVersion = client.BraveVersion
        };
    }

    public async Task<ExtensionConfigDto> GetConfigAsync(CancellationToken ct)
    {
        var config = await GetOrCreateAppConfigAsync(ct);
        var requiresSnapshot = false;
        
        // Simple heuristic: if we have zero bookmarks, request a snapshot.
        if (!await db.BookmarkNodes.AnyAsync(n => !n.IsDeleted, ct))
        {
            requiresSnapshot = true;
        }

        return new ExtensionConfigDto
        {
            ConfigVersion = config.ConfigVersion,
            PollIntervalSeconds = config.PollIntervalSeconds,
            SnapshotRequest = requiresSnapshot ? new SnapshotRequestDto
            {
                RequestId = Guid.NewGuid(),
                Reason = SnapshotReason.InitialImport
            } : null
        };
    }

}
