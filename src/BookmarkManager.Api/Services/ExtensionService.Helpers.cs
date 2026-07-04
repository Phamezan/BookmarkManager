using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services;

public sealed partial class ExtensionService
{
    private static readonly Guid BrowserNodeIdNamespace = Guid.Parse("8B3E4A1C-2D5F-4A7B-9C8E-1F6D0B3A2C4E");

    private static Guid CreateNodeId(string browserNodeId)
    {
        var nsBytes = BrowserNodeIdNamespace.ToByteArray();
        var inputBytes = Encoding.UTF8.GetBytes(browserNodeId);
        var combined = new byte[nsBytes.Length + inputBytes.Length];
        Buffer.BlockCopy(nsBytes, 0, combined, 0, nsBytes.Length);
        Buffer.BlockCopy(inputBytes, 0, combined, nsBytes.Length, inputBytes.Length);
        var hash = MD5.HashData(combined);
        hash[7] = (byte)((hash[7] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }

    private async Task<string?> BuildFolderPathAsync(Guid? folderId, CancellationToken ct)
    {
        if (!folderId.HasValue)
            return null;

        var titles = new Stack<string>();
        var currentId = folderId;
        for (var depth = 0; currentId.HasValue && depth < 32; depth++)
        {
            var folder = await db.BookmarkNodes
                .AsNoTracking()
                .Where(n => n.Id == currentId.Value && n.Type == NodeType.Folder && !n.IsDeleted)
                .Select(n => new { n.Title, n.ParentId })
                .FirstOrDefaultAsync(ct);

            if (folder is null)
                break;

            titles.Push(folder.Title);
            currentId = folder.ParentId;
        }

        return titles.Count == 0 ? null : string.Join(" / ", titles);
    }

    private async Task<AppConfig> GetOrCreateAppConfigAsync(CancellationToken ct)
    {
        var config = await db.AppConfig.FirstOrDefaultAsync(c => c.Id == AppConfigConstants.SingletonId, ct);
        if (config is not null)
        {
            return config;
        }

        config = new AppConfig
        {
            Id = AppConfigConstants.SingletonId,
            ConfigVersion = 1,
            PollIntervalSeconds = AppConfigConstants.DefaultPollIntervalSeconds,
            UpdatedAt = DateTime.UtcNow
        };
        db.AppConfig.Add(config);
        await db.SaveChangesAsync(ct);
        return config;
    }

}
