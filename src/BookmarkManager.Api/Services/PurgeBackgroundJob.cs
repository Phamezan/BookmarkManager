using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services;

public class PurgeBackgroundJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PurgeBackgroundJob> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(24);

    public PurgeBackgroundJob(IServiceScopeFactory scopeFactory, ILogger<PurgeBackgroundJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Purge background job started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeExpiredBookmarksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during database purge operation.");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private async Task PurgeExpiredBookmarksAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;
        var expiredNodes = await db.BookmarkNodes
            .Where(n => n.IsDeleted && n.PurgeAfter != null && n.PurgeAfter <= now)
            .ToListAsync(ct);

        if (expiredNodes.Count == 0)
        {
            _logger.LogInformation("No expired bookmarks found to purge.");
            return;
        }

        _logger.LogInformation("Found {Count} expired bookmark nodes to purge. Creating safety backup...", expiredNodes.Count);

        // 1. Create safety snapshot of purged nodes
        var backupDir = GetPurgeBackupsDirectory();
        var backupPath = Path.Combine(backupDir, $"purged_bookmarks_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");

        var serializedNodes = expiredNodes.Select(n => new
        {
            n.Id,
            n.ParentId,
            n.Type,
            n.Title,
            n.Url,
            n.Position,
            n.DeletedAt,
            n.PurgeAfter,
            n.UpdatedAt
        }).ToList();

        var json = JsonSerializer.Serialize(serializedNodes, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(backupPath, json, ct);

        _logger.LogInformation("Safety snapshot saved to {Path}. Proceeding with database purge.", backupPath);

        // 2. Permanently delete the nodes
        db.BookmarkNodes.RemoveRange(expiredNodes);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Database purge operation completed successfully.");
    }

    private string GetPurgeBackupsDirectory()
    {
        var dir = "/data/backups/purged";
        if (!Directory.Exists("/data"))
        {
            dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backups", "purged");
        }
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }
}
