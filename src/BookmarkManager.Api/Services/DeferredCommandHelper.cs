using System.Text.Json.Nodes;
using BookmarkManager.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services;

/// <summary>
/// Deferral mechanism for extension commands whose target parent folder has
/// no confirmed <see cref="BookmarkNode.BrowserNodeId"/> yet. Sending such a
/// command immediately would target the bookmark-bar root ("0") and drop the
/// node in the wrong place. Instead the command is enqueued with
/// <see cref="DeferredStatus"/> (invisible to the claim loop, which only
/// serves "Pending"), and promoted to Pending — with the payload's
/// parentBrowserNodeId rewritten to the real id — when the extension confirms
/// the parent folder's creation. Generalizes the pattern originally in
/// <see cref="BrokenLinksFolderHelper"/>.
/// </summary>
public static class DeferredCommandHelper
{
    public const string DeferredStatus = "Deferred";
    public const string PendingStatus = "Pending";

    /// <summary>
    /// Initial status for a command targeting <paramref name="parentNode"/>:
    /// Deferred when the parent exists but Brave has not confirmed its id yet,
    /// Pending otherwise. A null parent means the true root — never deferred.
    /// </summary>
    public static string InitialStatus(BookmarkNode? parentNode) =>
        parentNode is not null && string.IsNullOrEmpty(parentNode.BrowserNodeId)
            ? DeferredStatus
            : PendingStatus;

    /// <summary>
    /// Promotes deferred commands that were waiting on
    /// <paramref name="confirmedFolder"/>'s browser id: commands for its
    /// children (Create/Move/Restore into the folder) and for the folder
    /// itself (Reorder). Rewrites each payload's parentBrowserNodeId and sets
    /// the command Pending. Does not save — the caller owns the transaction so
    /// promotion is atomic with recording the folder's browser id.
    /// </summary>
    public static async Task PromoteDeferredCommandsAsync(
        AppDbContext db,
        BookmarkNode confirmedFolder,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(confirmedFolder.BrowserNodeId))
            return;

        var waitingIds = await db.BookmarkNodes
            .Where(n => n.ParentId == confirmedFolder.Id)
            .Select(n => n.Id)
            .ToListAsync(ct);
        waitingIds.Add(confirmedFolder.Id);

        var deferred = await db.ExtensionCommands
            .Where(c => c.Status == DeferredStatus && waitingIds.Contains(c.BookmarkId))
            .ToListAsync(ct);

        foreach (var cmd in deferred)
        {
            cmd.PayloadJson = RewriteParentBrowserNodeId(cmd.PayloadJson, confirmedFolder.BrowserNodeId!);

            // Reorder commands target the folder itself and carry its browser
            // id on the command row too.
            if (cmd.BookmarkId == confirmedFolder.Id)
            {
                cmd.BrowserNodeId = confirmedFolder.BrowserNodeId;
            }

            cmd.Status = PendingStatus;
        }
    }

    /// <summary>
    /// Replaces the top-level "parentBrowserNodeId" property in a command
    /// payload with the confirmed browser id. Returns the payload unchanged
    /// when it is not a JSON object or lacks the property.
    /// </summary>
    public static string? RewriteParentBrowserNodeId(string? payloadJson, string browserNodeId)
    {
        if (string.IsNullOrEmpty(payloadJson))
            return payloadJson;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payloadJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return payloadJson;
        }

        if (root is not JsonObject obj || !obj.ContainsKey("parentBrowserNodeId"))
            return payloadJson;

        obj["parentBrowserNodeId"] = browserNodeId;
        return obj.ToJsonString();
    }
}
