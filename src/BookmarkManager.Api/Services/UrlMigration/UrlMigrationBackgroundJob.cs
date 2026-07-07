using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Orchestrates URL Migrator v2 runs (plan section 6.5). Modeled on the retired
/// <c>DomainTriageBackgroundJob</c>: unbounded channel, single-flight <see cref="Enqueue"/>,
/// status snapshot under lock.
/// </summary>
public sealed class UrlMigrationBackgroundJob : BackgroundService
{
    public const string LivenessAbortMessage =
        "Domain appears alive — run Link Checker first or double-check the host.";

    private const int MaxCandidatesToVerify = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UrlMigrationBackgroundJob> _logger;
    private readonly Channel<UrlMigrationRunRequest> _requestChannel = Channel.CreateUnbounded<UrlMigrationRunRequest>();

    private readonly object _statusLock = new();
    private bool _isRunning;
    private Guid? _runId;
    private string? _deadHost;
    private int _totalFound;
    private int _processed;
    private int _resolved;
    private int _unresolved;
    private string? _currentBookmarkTitle;
    private string? _errorMessage;

    public UrlMigrationBackgroundJob(IServiceScopeFactory scopeFactory, ILogger<UrlMigrationBackgroundJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Single-flight enqueue: returns false when a run is already active.</summary>
    public bool Enqueue(string deadHost)
    {
        Guid runId;
        lock (_statusLock)
        {
            if (_isRunning)
            {
                return false;
            }

            runId = Guid.NewGuid();
            _isRunning = true;
            _runId = runId;
            _deadHost = deadHost;
            _totalFound = 0;
            _processed = 0;
            _resolved = 0;
            _unresolved = 0;
            _currentBookmarkTitle = null;
            _errorMessage = null;
        }

        var queued = _requestChannel.Writer.TryWrite(new UrlMigrationRunRequest(runId, deadHost));
        if (!queued)
        {
            lock (_statusLock)
            {
                _isRunning = false;
            }
        }

        return queued;
    }

    public UrlMigrationStatusDto GetStatus()
    {
        lock (_statusLock)
        {
            return new UrlMigrationStatusDto
            {
                IsRunning = _isRunning,
                RunId = _runId,
                DeadHost = _deadHost,
                TotalFound = _totalFound,
                Processed = _processed,
                Resolved = _resolved,
                Unresolved = _unresolved,
                CurrentBookmarkTitle = _currentBookmarkTitle,
                ErrorMessage = _errorMessage
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("URL migration background job started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var request = await _requestChannel.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);

                try
                {
                    await RunMigrationAsync(request, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "URL migration run failed.");
                    lock (_statusLock)
                    {
                        _errorMessage = ex.Message;
                    }
                }
                finally
                {
                    lock (_statusLock)
                    {
                        _isRunning = false;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "URL migration background job loop encountered an error.");
            }
        }
    }

    private async Task RunMigrationAsync(UrlMigrationRunRequest request, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var livenessGuard = scope.ServiceProvider.GetRequiredService<IDomainLivenessGuard>();
        var extractionService = scope.ServiceProvider.GetRequiredService<ISeriesExtractionService>();
        var searchService = scope.ServiceProvider.GetRequiredService<IAlternativeUrlSearchService>();
        var verificationService = scope.ServiceProvider.GetRequiredService<ICandidateVerificationService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<AiTaggingSettingsService>();
        var approvalService = scope.ServiceProvider.GetRequiredService<UrlMigrationApprovalService>();

        var deadHost = request.DeadHost;

        var candidateBookmarks = await db.BookmarkNodes
            .Where(n => n.Type == NodeType.Bookmark && !n.IsDeleted && n.Url != null)
            .ToListAsync(ct).ConfigureAwait(false);

        var hostMatched = candidateBookmarks.Where(n => HostMatches(n.Url!, deadHost)).ToList();
        if (hostMatched.Count == 0)
        {
            return;
        }

        // Liveness sanity check runs against every host-matched bookmark, independent of the
        // re-run skip below, since it's judging the domain itself.
        var isAlive = await livenessGuard.IsDomainAliveAsync(hostMatched.Select(m => m.Url!), ct).ConfigureAwait(false);
        if (isAlive)
        {
            lock (_statusLock)
            {
                _errorMessage = LivenessAbortMessage;
            }
            return;
        }

        // Re-run safe: skip bookmarks that already have a Pending proposal for this host from an
        // earlier (possibly interrupted) run.
        var alreadyPendingIds = await db.UrlMigrationProposals
            .Where(p => p.DeadHost == deadHost && p.Status == "Pending")
            .Select(p => p.BookmarkId)
            .ToListAsync(ct).ConfigureAwait(false);
        var alreadyPending = new HashSet<Guid>(alreadyPendingIds);

        var toProcess = hostMatched.Where(m => !alreadyPending.Contains(m.Id)).ToList();
        if (toProcess.Count == 0)
        {
            return;
        }

        lock (_statusLock)
        {
            _totalFound = toProcess.Count;
        }

        var extractions = await ExtractBatchAsync(extractionService, toProcess, ct).ConfigureAwait(false);
        var aiSettings = await settingsService.GetAsync(ct).ConfigureAwait(false);

        for (var i = 0; i < toProcess.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var bookmark = toProcess[i];
            var extraction = extractions[i];

            lock (_statusLock)
            {
                _currentBookmarkTitle = bookmark.Title;
            }

            var proposal = await BuildProposalAsync(
                request.RunId, deadHost, bookmark, extraction, searchService, verificationService, ct).ConfigureAwait(false);

            db.UrlMigrationProposals.Add(proposal);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            lock (_statusLock)
            {
                _processed++;
                if (proposal.Confidence == "Unresolved")
                {
                    _unresolved++;
                }
                else
                {
                    _resolved++;
                }
            }

            if (aiSettings.MigrationAutoApproveHigh && proposal.Confidence == "High")
            {
                try
                {
                    await approvalService.ApproveAsync([proposal.Id], ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-approve failed for URL migration proposal {ProposalId}", proposal.Id);
                }
            }
        }
    }

    private static async Task<IReadOnlyList<SeriesExtraction>> ExtractBatchAsync(
        ISeriesExtractionService extractionService,
        IReadOnlyList<BookmarkNode> bookmarks,
        CancellationToken ct)
    {
        if (extractionService is GroqSeriesExtractionService groqExtraction)
        {
            var items = bookmarks
                .Select(b => new SeriesExtractionRequestItem(b.Title, b.Url!, b.Category))
                .ToList();
            return await groqExtraction.ExtractBatchAsync(items, ct).ConfigureAwait(false);
        }

        var results = new List<SeriesExtraction>(bookmarks.Count);
        foreach (var bookmark in bookmarks)
        {
            results.Add(await extractionService.ExtractAsync(bookmark.Title, bookmark.Url!, bookmark.Category, ct).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<UrlMigrationProposal> BuildProposalAsync(
        Guid runId,
        string deadHost,
        BookmarkNode bookmark,
        SeriesExtraction extraction,
        IAlternativeUrlSearchService searchService,
        ICandidateVerificationService verificationService,
        CancellationToken ct)
    {
        var proposal = new UrlMigrationProposal
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            BookmarkId = bookmark.Id,
            DeadHost = deadHost,
            OldUrl = bookmark.Url!,
            SeriesName = extraction.SeriesName,
            ChapterNumber = extraction.ChapterNumber,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        IReadOnlyList<SearchCandidate> candidates;
        try
        {
            candidates = await searchService.SearchAsync(extraction, deadHost, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search failed for bookmark {BookmarkId}; proposal will be Unresolved.", bookmark.Id);
            candidates = [];
        }

        if (candidates.Count == 0)
        {
            proposal.Confidence = "Unresolved";
            proposal.Detail = "No search candidates found.";
            return proposal;
        }

        SearchCandidate? bestSeriesMatch = null;
        VerificationResult? bestSeriesMatchResult = null;
        SearchCandidate? challengeCandidate = null;
        VerificationResult? challengeResult = null;

        foreach (var candidate in candidates.Take(MaxCandidatesToVerify))
        {
            ct.ThrowIfCancellationRequested();

            VerificationResult result;
            try
            {
                result = await verificationService.VerifyAsync(candidate, extraction, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Verification failed for candidate {Url}", candidate.Url);
                continue;
            }

            if (result.Reachable && result.SeriesMatched)
            {
                bestSeriesMatch = candidate;
                bestSeriesMatchResult = result;
                break; // Stop at first candidate that passes (plan §6.4).
            }

            if (challengeCandidate == null && result.Detail.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase))
            {
                challengeCandidate = candidate;
                challengeResult = result;
            }
        }

        if (bestSeriesMatch != null)
        {
            ApplySeriesMatch(proposal, bestSeriesMatch, bestSeriesMatchResult!, extraction);

            if (proposal.Confidence != "High" && !string.IsNullOrWhiteSpace(extraction.ChapterNumber))
            {
                var deepLink = await TryChapterDeepLinksAsync(bestSeriesMatch.Url, extraction, verificationService, ct).ConfigureAwait(false);
                if (deepLink != null)
                {
                    proposal.ProposedUrl = deepLink;
                    proposal.ProposedHost = TryGetHost(deepLink);
                    proposal.Confidence = "High";
                    proposal.Detail = "Series and chapter matched (chapter deep-link fallback).";
                }
            }

            return proposal;
        }

        if (challengeCandidate != null)
        {
            proposal.ProposedUrl = challengeCandidate.Url;
            proposal.ProposedHost = TryGetHost(challengeCandidate.Url);
            proposal.Confidence = "Low";
            proposal.Detail = challengeResult!.Detail;
            return proposal;
        }

        proposal.Confidence = "Unresolved";
        proposal.Detail = "No candidate survived verification.";
        return proposal;
    }

    private static void ApplySeriesMatch(
        UrlMigrationProposal proposal, SearchCandidate candidate, VerificationResult result, SeriesExtraction extraction)
    {
        proposal.ProposedUrl = candidate.Url;
        proposal.ProposedHost = TryGetHost(candidate.Url);

        if (result.ChapterMatched)
        {
            proposal.Confidence = "High";
            proposal.Detail = result.Detail;
            return;
        }

        var chapterText = string.IsNullOrWhiteSpace(extraction.ChapterNumber) ? "unknown" : extraction.ChapterNumber;
        proposal.Confidence = "Medium";
        proposal.Detail = $"series page only — was at chapter {chapterText}. progress: chapter {chapterText} (from old URL)";
    }

    /// <summary>
    /// Chapter deep-link fallback (plan §2): when the search only found a series front page, try
    /// constructed deep links before settling for Medium confidence.
    /// </summary>
    private static async Task<string?> TryChapterDeepLinksAsync(
        string seriesUrl, SeriesExtraction extraction, ICandidateVerificationService verificationService, CancellationToken ct)
    {
        var trimmed = seriesUrl.TrimEnd('/');
        var chapter = extraction.ChapterNumber;
        string[] deepLinks =
        [
            $"{trimmed}/chapter-{chapter}",
            $"{trimmed}/chapter-{chapter}/",
            $"{trimmed}/{chapter}"
        ];

        foreach (var link in deepLinks)
        {
            ct.ThrowIfCancellationRequested();

            VerificationResult result;
            try
            {
                result = await verificationService.VerifyAsync(new SearchCandidate(link, null, null), extraction, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            if (result.Reachable && result.SeriesMatched && result.ChapterMatched)
            {
                return link;
            }
        }

        return null;
    }

    private static bool HostMatches(string url, string deadHost)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals(deadHost, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith("." + deadHost, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private sealed record UrlMigrationRunRequest(Guid RunId, string DeadHost);
}
