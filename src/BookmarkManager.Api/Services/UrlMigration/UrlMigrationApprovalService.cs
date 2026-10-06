using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Infrastructure;
using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Approve/reject/revert for URL Migrator v2 proposals (plan section 6.6). Approve/revert each
/// run in their own DB transaction per proposal (sync invariant: projection update + command
/// enqueue atomically). <see cref="SyncWebSocketManager.BroadcastSyncAsync"/> fires once per
/// batch, not per proposal.
///
/// Suwayomi proposals (those carrying a <see cref="UrlMigrationProposal.SuwayomiMangaId"/>) run
/// their Suwayomi side effect (add to library, mark chapters up to the bookmark read) before the
/// DB transaction; if any Suwayomi call fails the approval fails and the proposal stays Pending.
/// </summary>
public sealed class UrlMigrationApprovalService
{
    private const string Approved = "Approved";
    private const string Rejected = "Rejected";
    private const string Reverted = "Reverted";
    private const string Pending = "Pending";

    private const string SuwayomiUnreachableMessage = "Suwayomi didn't respond — nothing was changed.";
    private const string NothingMarkedRead = "nothing marked read";

    private readonly AppDbContext _db;
    private readonly ILogger<UrlMigrationApprovalService> _logger;
    private readonly ISuwayomiClient _suwayomi;
    private readonly SuwayomiOptions _suwayomiOptions;

    public UrlMigrationApprovalService(
        AppDbContext db,
        ILogger<UrlMigrationApprovalService> logger,
        ISuwayomiClient suwayomi,
        IOptions<SuwayomiOptions> suwayomiOptions)
    {
        _db = db;
        _logger = logger;
        _suwayomi = suwayomi;
        _suwayomiOptions = suwayomiOptions.Value;
    }

    public async Task<DecideProposalsResponse> ApproveAsync(IReadOnlyCollection<Guid> proposalIds, CancellationToken ct)
    {
        var errors = new List<string>();
        var messages = new List<string>();
        var succeeded = 0;
        var broadcastNeeded = false;

        foreach (var id in proposalIds)
        {
            ct.ThrowIfCancellationRequested();

            // The Suwayomi side effect runs BEFORE the DB transaction: its HTTP calls can take tens
            // of seconds (Cloudflare-bypassed sources) and must not hold SQLite's write lock, which
            // would stall extension sync. A failure leaves the proposal Pending and the bookmark
            // untouched (no half-applied state).
            var pre = await _db.UrlMigrationProposals.AsNoTracking()
                .Where(p => p.Id == id)
                .Select(p => new { p.Status, p.SuwayomiMangaId, p.ChapterNumber })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            SuwayomiApplyResult? suwayomiResult = null;
            if (pre is { SuwayomiMangaId: int mangaId } && string.Equals(pre.Status, Pending, StringComparison.Ordinal))
            {
                try
                {
                    suwayomiResult = await ApplySuwayomiAsync(pre.ChapterNumber, mangaId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Suwayomi approval failed for proposal {ProposalId}", id);
                    errors.Add(SuwayomiUnreachableMessage);
                    continue;
                }
            }

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

                var suwayomiMangaId = suwayomiResult is null ? null : proposal.SuwayomiMangaId;

                var oldHost = proposal.DeadHost;
                var newHost = proposal.ProposedHost ?? proposedUri.Host;

                bookmark.PreviousUrl = bookmark.Url;
                bookmark.Url = proposal.ProposedUrl;

                // Titles scraped from the dead site are usually boilerplate-laden with the old
                // site's own name in them ("... on Aniwatch.to", "... - ReaperScans") - keeping
                // that after migrating to a different site is actively misleading, so replace it
                // with a clean "Series - Chapter/Episode N" title built from what the migrator
                // already extracted. Original is kept in PreviousTitle so Revert can restore it.
                // Suwayomi proposals use the source's own clean series title verbatim.
                var cleanTitle = proposal.MatchedTitle is { Length: > 0 } matchedTitle
                    ? matchedTitle
                    : BuildCleanTitle(proposal.SeriesName, proposal.ChapterNumber);
                if (!string.IsNullOrWhiteSpace(cleanTitle) && !string.Equals(cleanTitle, bookmark.Title, StringComparison.Ordinal))
                {
                    bookmark.PreviousTitle = bookmark.Title;
                    bookmark.Title = cleanTitle;
                }

                if (suwayomiResult is not null)
                {
                    bookmark.SuwayomiMangaId = suwayomiMangaId;
                    bookmark.SourceUrl = suwayomiResult.RealUrl;
                    if (SuwayomiMatchScorer.ProgressFromChapter(proposal.ChapterNumber) is int progress)
                    {
                        bookmark.CurrentProgress = progress;
                    }

                    messages.Add(suwayomiResult.Note);

                    if (suwayomiResult.Note == NothingMarkedRead
                        && !(proposal.Detail?.Contains("marked read", StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        proposal.Detail = string.IsNullOrWhiteSpace(proposal.Detail)
                            ? "Nothing marked read."
                            : $"{proposal.Detail} Nothing marked read.";
                    }
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

        return new DecideProposalsResponse(succeeded, proposalIds.Count - succeeded, errors,
            messages.Count > 0 ? messages : null);
    }

    /// <summary>
    /// Adds the manga to the Suwayomi library if needed and marks every chapter up to the
    /// bookmarked chapter read (chapter 0 included). When the chapter is unknown or beyond the
    /// source's highest chapter, nothing is marked. On failure, a library add performed by this
    /// call is rolled back best-effort before the exception propagates.
    /// </summary>
    private async Task<SuwayomiApplyResult> ApplySuwayomiAsync(
        string? chapterNumber, int mangaId, CancellationToken ct)
    {
        var details = await _suwayomi.GetMangaAndChaptersAsync(mangaId, ct).ConfigureAwait(false);
        var addedToLibrary = false;
        try
        {
            if (!details.Manga.InLibrary)
            {
                await _suwayomi.AddToLibraryAsync(mangaId, ct).ConfigureAwait(false);
                addedToLibrary = true;
            }

            var target = SuwayomiMatchScorer.ParseChapterNumber(chapterNumber);
            double? highest = details.Chapters.Count > 0
                ? details.Chapters.Max(c => c.ChapterNumber)
                : null;

            if (target is null || highest is null || target.Value > highest.Value)
            {
                return new SuwayomiApplyResult(details.Manga.RealUrl, NothingMarkedRead);
            }

            var ids = SuwayomiMatchScorer.SelectChaptersToMark(details.Chapters, chapterNumber);
            await _suwayomi.MarkChaptersReadAsync(ids, ct).ConfigureAwait(false);
            return new SuwayomiApplyResult(details.Manga.RealUrl, $"{ids.Count} chapters marked read");
        }
        catch
        {
            if (addedToLibrary)
            {
                try
                {
                    await _suwayomi.RemoveFromLibraryAsync(mangaId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to roll back Suwayomi library add for manga {MangaId}", mangaId);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Re-points a Pending proposal at a manga the user picked manually (refreshing the source's
    /// latest chapter), sets Confidence to "Manual", and approves it through the normal path.
    /// </summary>
    public async Task<DecideProposalsResponse> MatchSuwayomiAndApproveAsync(
        Guid proposalId, int mangaId, string sourceName, string title, CancellationToken ct)
    {
        if (mangaId <= 0 || string.IsNullOrWhiteSpace(sourceName))
        {
            return new DecideProposalsResponse(0, 1, ["mangaId and sourceName are required."]);
        }

        var proposal = await _db.UrlMigrationProposals
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct).ConfigureAwait(false);
        if (proposal == null || !string.Equals(proposal.Status, Pending, StringComparison.Ordinal))
        {
            return new DecideProposalsResponse(0, 1, [$"Proposal {proposalId} not found or not Pending."]);
        }

        string? latest = null;
        try
        {
            var details = await _suwayomi.GetMangaAndChaptersAsync(mangaId, ct).ConfigureAwait(false);
            if (details.Chapters.Count > 0)
            {
                latest = FormatChapter(details.Chapters.Max(c => c.ChapterNumber));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Suwayomi match lookup failed for proposal {ProposalId}", proposalId);
            return new DecideProposalsResponse(0, 1, [SuwayomiUnreachableMessage]);
        }

        proposal.IsSuwayomi = true;
        proposal.SuwayomiMangaId = mangaId;
        proposal.SourceName = sourceName.Trim();
        proposal.MatchedTitle = string.IsNullOrWhiteSpace(title) ? sourceName.Trim() : title.Trim();
        proposal.SourceLatestChapter = latest;
        proposal.ProposedUrl = $"{_suwayomiOptions.PublicBaseUrl.TrimEnd('/')}/manga/{mangaId}";
        proposal.ProposedHost = Infrastructure.UrlHelpers.TryGetHost(proposal.ProposedUrl);
        proposal.Confidence = "Manual";
        proposal.Detail = latest is null
            ? $"{sourceName.Trim()} · chapter list unavailable"
            : $"{sourceName.Trim()} · latest ch {latest}";
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return await ApproveAsync([proposalId], ct).ConfigureAwait(false);
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

    public async Task<UrlMigrationProposalDto?> UpdateProposedUrlAsync(
        Guid proposalId,
        string url,
        ICandidateVerificationService verificationService,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var proposal = await _db.UrlMigrationProposals
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct).ConfigureAwait(false);
        if (proposal == null || !string.Equals(proposal.Status, Pending, StringComparison.Ordinal))
        {
            return null;
        }

        var bookmark = await _db.BookmarkNodes
            .FirstOrDefaultAsync(b => b.Id == proposal.BookmarkId, ct).ConfigureAwait(false);

        proposal.ProposedUrl = url;
        proposal.ProposedHost = uri.Host;

        // Verify the edited candidate URL
        var extraction = new SeriesExtraction(proposal.SeriesName ?? bookmark?.Title ?? string.Empty, proposal.ChapterNumber, "unknown", false);
        var verification = await verificationService.VerifyAsync(new SearchCandidate(url, null, null), extraction, ct).ConfigureAwait(false);

        if (verification.Reachable && verification.SeriesMatched)
        {
            proposal.Confidence = verification.ChapterMatched ? "High" : "Medium";
            proposal.Detail = $"Manual edit verified: {verification.Detail}";
        }
        else if (verification.Reachable)
        {
            proposal.Confidence = "Low";
            proposal.Detail = $"Manual edit reachable but series did not match: {verification.Detail}";
        }
        else
        {
            proposal.Confidence = "Low";
            proposal.Detail = $"Manual edit unverified: {verification.Detail}";
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        return ToDto(proposal, bookmark);
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

            // Only drop the series from the Suwayomi library when no other bookmark still links it
            // (duplicate bookmarks of one series share a manga id).
            int? mangaIdToRemove = null;
            if (proposal.SuwayomiMangaId is int suwayomiMangaId &&
                !await _db.BookmarkNodes.AnyAsync(b => b.Id != bookmark.Id && !b.IsDeleted && b.SuwayomiMangaId == suwayomiMangaId, ct).ConfigureAwait(false))
            {
                mangaIdToRemove = suwayomiMangaId;
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

            if (proposal.SuwayomiMangaId != null)
            {
                bookmark.SuwayomiMangaId = null;
                bookmark.SourceUrl = null;
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

            // Best-effort, after commit (never hold the SQLite write lock across HTTP); a Suwayomi
            // outage must never block the revert.
            if (mangaIdToRemove is int removeId)
            {
                try
                {
                    await _suwayomi.RemoveFromLibraryAsync(removeId, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Failed to remove manga {MangaId} from Suwayomi during revert", removeId);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            _logger.LogError(ex, "Failed to revert URL migration proposal {ProposalId}", proposalId);
            return false;
        }
    }

    private static UrlMigrationProposalDto ToDto(UrlMigrationProposal proposal, BookmarkNode? bookmark) => new()
    {
        Id = proposal.Id,
        BookmarkId = proposal.BookmarkId,
        BookmarkTitle = bookmark?.Title ?? string.Empty,
        OldUrl = proposal.OldUrl,
        ProposedUrl = proposal.ProposedUrl,
        ProposedHost = proposal.ProposedHost,
        SeriesName = proposal.SeriesName,
        ChapterNumber = proposal.ChapterNumber,
        Confidence = proposal.Confidence,
        Detail = proposal.Detail,
        Status = proposal.Status,
        CreatedAt = proposal.CreatedAt,
        IsSuwayomi = proposal.IsSuwayomi,
        SuwayomiMangaId = proposal.SuwayomiMangaId,
        SourceName = proposal.SourceName,
        MatchedTitle = proposal.MatchedTitle,
        SourceLatestChapter = proposal.SourceLatestChapter
    };

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

    private static string FormatChapter(double value)
        => value == Math.Truncate(value)
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

    private sealed record SuwayomiApplyResult(string? RealUrl, string Note);
}
