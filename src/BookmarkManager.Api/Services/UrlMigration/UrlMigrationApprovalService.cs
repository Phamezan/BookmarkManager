using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Infrastructure;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Approve/reject/revert for URL Migrator v2 proposals (plan section 6.6). Approve/revert each
/// run in their own DB transaction per proposal (sync invariant: projection update + command
/// enqueue atomically). <see cref="SyncWebSocketManager.BroadcastSyncAsync"/> fires once per
/// batch, not per proposal.
/// </summary>
public sealed class UrlMigrationApprovalService
{
    private const string Approved = "Approved";
    private const string Rejected = "Rejected";
    private const string Reverted = "Reverted";
    private const string Pending = "Pending";

    private readonly AppDbContext _db;
    private readonly ILogger<UrlMigrationApprovalService> _logger;

    public UrlMigrationApprovalService(AppDbContext db, ILogger<UrlMigrationApprovalService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<DecideProposalsResponse> ApproveAsync(IReadOnlyCollection<Guid> proposalIds, CancellationToken ct)
    {
        var errors = new List<string>();
        var succeeded = 0;
        var broadcastNeeded = false;

        foreach (var id in proposalIds)
        {
            ct.ThrowIfCancellationRequested();

            await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var proposal = await _db.UrlMigrationProposals
                    .FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
                if (proposal == null)
                {
                    errors.Add($"Proposal {id} not found.");
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (!string.Equals(proposal.Status, Pending, StringComparison.Ordinal))
                {
                    errors.Add($"Proposal {id} is not Pending (status: {proposal.Status}).");
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(proposal.ProposedUrl) ||
                    !Uri.TryCreate(proposal.ProposedUrl, UriKind.Absolute, out var proposedUri) ||
                    (proposedUri.Scheme != Uri.UriSchemeHttp && proposedUri.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add($"Proposal {id} does not have a valid http/https proposed URL.");
                    await transaction.RollbackAsync(ct).ConfigureAwait(false);
                    continue;
                }

                var bookmark = await _db.BookmarkNodes
                    .FirstOrDefaultAsync(b => b.Id == proposal.BookmarkId, ct).ConfigureAwait(false);
                if (bookmark == null || bookmark.IsDeleted)
                {
                    errors.Add($"Proposal {id}'s bookmark no longer exists.");
                    proposal.Status = Rejected;
                    proposal.DecidedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                var oldHost = proposal.DeadHost;
                var newHost = proposal.ProposedHost ?? proposedUri.Host;

                bookmark.PreviousUrl = bookmark.Url;
                bookmark.Url = proposal.ProposedUrl;

                // Titles scraped from the dead site are usually boilerplate-laden with the old
                // site's own name in them ("... on Aniwatch.to", "... - ReaperScans") - keeping
                // that after migrating to a different site is actively misleading, so replace it
                // with a clean "Series - Chapter/Episode N" title built from what the migrator
                // already extracted. Original is kept in PreviousTitle so Revert can restore it.
                var cleanTitle = BuildCleanTitle(proposal.SeriesName, proposal.ChapterNumber);
                if (!string.IsNullOrWhiteSpace(cleanTitle) && !string.Equals(cleanTitle, bookmark.Title, StringComparison.Ordinal))
                {
                    bookmark.PreviousTitle = bookmark.Title;
                    bookmark.Title = cleanTitle;
                }

                bookmark.Version++;
                bookmark.SyncState = SyncState.Pending;
                bookmark.UpdatedAt = DateTime.UtcNow;
                AppendNote(bookmark, BuildNote(oldHost, newHost, proposal.ChapterNumber));

                if (!string.IsNullOrEmpty(bookmark.BrowserNodeId))
                {
                    EnqueueUpdateCommand(bookmark);
                }

                proposal.Status = Approved;
                proposal.DecidedAt = DateTime.UtcNow;

                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                succeeded++;
                broadcastNeeded = true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                _logger.LogError(ex, "Failed to approve URL migration proposal {ProposalId}", id);
                errors.Add($"Proposal {id}: {ex.Message}");
            }
        }

        if (broadcastNeeded)
        {
            await SyncWebSocketManager.BroadcastSyncAsync().ConfigureAwait(false);
        }

        return new DecideProposalsResponse(succeeded, proposalIds.Count - succeeded, errors);
    }

    public async Task<DecideProposalsResponse> SetManualUrlAndApproveAsync(Guid proposalId, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new DecideProposalsResponse(0, 1, new List<string> { "Url must be an absolute http/https URL." });
        }

        var proposal = await _db.UrlMigrationProposals
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct).ConfigureAwait(false);
        if (proposal == null || !string.Equals(proposal.Status, Pending, StringComparison.Ordinal))
        {
            return new DecideProposalsResponse(0, 1, new List<string> { $"Proposal {proposalId} not found or not Pending." });
        }

        proposal.ProposedUrl = url;
        proposal.ProposedHost = uri.Host;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return await ApproveAsync(new[] { proposalId }, ct).ConfigureAwait(false);
    }

    public async Task<DecideProposalsResponse> RejectAsync(IReadOnlyCollection<Guid> proposalIds, CancellationToken ct)
    {
        var errors = new List<string>();
        var succeeded = 0;

        foreach (var id in proposalIds)
        {
            ct.ThrowIfCancellationRequested();

            var proposal = await _db.UrlMigrationProposals
                .FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
            if (proposal == null)
            {
                errors.Add($"Proposal {id} not found.");
                continue;
            }

            if (!string.Equals(proposal.Status, Pending, StringComparison.Ordinal))
            {
                errors.Add($"Proposal {id} is not Pending (status: {proposal.Status}).");
                continue;
            }

            proposal.Status = Rejected;
            proposal.DecidedAt = DateTime.UtcNow;
            succeeded++;
        }

        if (succeeded > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new DecideProposalsResponse(succeeded, proposalIds.Count - succeeded, errors);
    }

    /// <summary>
    /// Voids a stale Pending proposal without recording a decision. Unlike Reject (which marks
    /// the URL as "seen and declined" so future runs won't re-suggest it), Cancel deletes the row
    /// outright so the bookmark is simply eligible for a completely fresh run.
    /// </summary>
    public async Task<DecideProposalsResponse> CancelAsync(IReadOnlyCollection<Guid> proposalIds, CancellationToken ct)
    {
        var errors = new List<string>();
        var succeeded = 0;

        foreach (var id in proposalIds)
        {
            ct.ThrowIfCancellationRequested();

            var proposal = await _db.UrlMigrationProposals
                .FirstOrDefaultAsync(p => p.Id == id, ct).ConfigureAwait(false);
            if (proposal == null)
            {
                errors.Add($"Proposal {id} not found.");
                continue;
            }

            if (!string.Equals(proposal.Status, Pending, StringComparison.Ordinal))
            {
                errors.Add($"Proposal {id} is not Pending (status: {proposal.Status}).");
                continue;
            }

            _db.UrlMigrationProposals.Remove(proposal);
            succeeded++;
        }

        if (succeeded > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new DecideProposalsResponse(succeeded, proposalIds.Count - succeeded, errors);
    }

    public async Task<bool> RevertAsync(Guid proposalId, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var proposal = await _db.UrlMigrationProposals
                .FirstOrDefaultAsync(p => p.Id == proposalId, ct).ConfigureAwait(false);
            if (proposal == null || !string.Equals(proposal.Status, Approved, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            var bookmark = await _db.BookmarkNodes
                .FirstOrDefaultAsync(b => b.Id == proposal.BookmarkId, ct).ConfigureAwait(false);
            if (bookmark == null || bookmark.IsDeleted || string.IsNullOrWhiteSpace(bookmark.PreviousUrl))
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            var newHost = proposal.DeadHost;
            var oldHost = proposal.ProposedHost ?? TryGetHost(bookmark.Url) ?? "unknown";

            var restoredUrl = bookmark.PreviousUrl;
            bookmark.PreviousUrl = bookmark.Url;
            bookmark.Url = restoredUrl;

            if (bookmark.PreviousTitle != null)
            {
                var currentTitle = bookmark.Title;
                bookmark.Title = bookmark.PreviousTitle;
                bookmark.PreviousTitle = currentTitle;
            }

            bookmark.Version++;
            bookmark.SyncState = SyncState.Pending;
            bookmark.UpdatedAt = DateTime.UtcNow;
            AppendNote(bookmark, BuildNote(oldHost, newHost, proposal.ChapterNumber, reverted: true));

            if (!string.IsNullOrEmpty(bookmark.BrowserNodeId))
            {
                EnqueueUpdateCommand(bookmark);
            }

            proposal.Status = Reverted;
            proposal.DecidedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await SyncWebSocketManager.BroadcastSyncAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            _logger.LogError(ex, "Failed to revert URL migration proposal {ProposalId}", proposalId);
            return false;
        }
    }

    private static string? BuildCleanTitle(string? seriesName, string? chapterNumber)
    {
        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(chapterNumber)
            ? seriesName.Trim()
            : $"{seriesName.Trim()} - {chapterNumber.Trim()}";
    }

    private void EnqueueUpdateCommand(BookmarkNode bookmark)
    {
        var updatePayload = new { title = bookmark.Title, url = bookmark.Url };
        _db.ExtensionCommands.Add(new ExtensionCommandEntry
        {
            Id = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            CommandType = "Update",
            BookmarkId = bookmark.Id,
            BrowserNodeId = bookmark.BrowserNodeId,
            ExpectedVersion = bookmark.Version - 1,
            PayloadJson = JsonSerializer.Serialize(updatePayload),
            CreatedAt = DateTime.UtcNow,
            Status = "Pending"
        });
    }

    private static void AppendNote(BookmarkNode bookmark, string note)
    {
        bookmark.Notes = string.IsNullOrWhiteSpace(bookmark.Notes) ? note : $"{bookmark.Notes}\n{note}";
    }

    private static string BuildNote(string oldHost, string newHost, string? chapterNumber, bool reverted = false)
    {
        var chapter = string.IsNullOrWhiteSpace(chapterNumber) ? "unknown" : chapterNumber;
        var prefix = reverted ? "[URL Migrator] Reverted " : "[URL Migrator] ";
        return $"{prefix}{oldHost} → {newHost} on {DateTime.UtcNow:yyyy-MM-dd}. Progress: chapter {chapter}.";
    }

    private static string? TryGetHost(string? url) => Infrastructure.UrlHelpers.TryGetHost(url);
}
