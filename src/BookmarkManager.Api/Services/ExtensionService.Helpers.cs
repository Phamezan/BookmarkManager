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

    public async Task<ExtensionBookmarkEnrichmentDto?> GetBookmarkEnrichmentByBrowserIdAsync(
        string browserNodeId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(browserNodeId))
            return null;

        var node = await db.BookmarkNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.BrowserNodeId == browserNodeId && !n.IsDeleted, ct);
        if (node is null || node.Type != NodeType.Bookmark)
            return null;

        var folderPath = await Data.FolderHierarchy.BuildFolderPathAsync(db, node.ParentId, ct);
        var tags = string.IsNullOrWhiteSpace(node.Tags)
            ? Array.Empty<string>()
            : node.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Cover precedence mirrors BookmarksController.Queries.cs EnrichCoverImagesRecursive:
        // catalog match cover wins when present, else fall back to the node's own stored cover.
        var matches = await matchService.GetMatchesAsync(ct);
        var matchedCover = matches.FirstOrDefault(m => m.BookmarkId == node.Id)?.CoverImageUrl;
        var coverImageUrl = !string.IsNullOrWhiteSpace(matchedCover) ? matchedCover : node.CoverImageUrl;

        return new ExtensionBookmarkEnrichmentDto(node.Id, node.Title, folderPath, tags, node.Status, coverImageUrl);
    }

    public async Task<bool> SetBookmarkCoverByBrowserIdAsync(
        string browserNodeId,
        string? coverImageUrl,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(browserNodeId))
            return false;

        var node = await db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.BrowserNodeId == browserNodeId && !n.IsDeleted, ct);
        if (node is null || node.Type != NodeType.Bookmark)
            return false;

        // Don't clobber an existing cover — catalog/library covers win.
        if (!string.IsNullOrWhiteSpace(node.CoverImageUrl))
            return true;
        if (string.IsNullOrWhiteSpace(coverImageUrl))
            return false;

        node.CoverImageUrl = coverImageUrl;
        node.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
