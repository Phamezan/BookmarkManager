using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services;

public sealed partial class ExtensionService
{
    public async Task ResetDatabaseAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            db.BookmarkNodes.RemoveRange(db.BookmarkNodes);
            db.ExtensionCommands.RemoveRange(db.ExtensionCommands);
            db.ExtensionEvents.RemoveRange(db.ExtensionEvents);
            db.SnapshotNodeMappings.RemoveRange(db.SnapshotNodeMappings);
            db.SnapshotBatches.RemoveRange(db.SnapshotBatches);
            db.ExtensionClients.RemoveRange(db.ExtensionClients);
            db.ActivityLog.RemoveRange(db.ActivityLog);
            db.AppConfig.RemoveRange(db.AppConfig);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }

        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
    }

}
