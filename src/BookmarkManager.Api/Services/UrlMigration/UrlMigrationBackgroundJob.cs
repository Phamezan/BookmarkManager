using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
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
public sealed partial class UrlMigrationBackgroundJob : BackgroundService
{
    public static readonly TimeSpan DefaultRunTimeout = TimeSpan.FromMinutes(30);

    public const string LivenessAbortMessage =
        "Domain appears alive — run Link Checker first or double-check the host.";

    private const int MaxCandidatesToVerify = 5;
    private const int MaxDiagnosticEntries = 5;
    private const int MaxDiagnosticChars = 400;

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
    private readonly Dictionary<string, int> _failureReasons = new();
    private CancellationTokenSource? _activeRunCancellation;

    /// <summary>
    /// Maximum wall-clock duration of one migration run. Public to allow integration tests and
    /// host configuration to select a short deterministic timeout without delaying in real time.
    /// </summary>
    public TimeSpan RunTimeout { get; set; } = DefaultRunTimeout;
    public int MaxConcurrency { get; set; } = 4;

    public UrlMigrationBackgroundJob(IServiceScopeFactory scopeFactory, ILogger<UrlMigrationBackgroundJob> logger, IConfiguration? configuration = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        if (double.TryParse(configuration?["UrlMigration:RunTimeoutMinutes"], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var minutes) && minutes > 0 && minutes <= 1440)
            RunTimeout = TimeSpan.FromMinutes(minutes);
        if (int.TryParse(configuration?["UrlMigration:MaxConcurrency"], out var concurrency))
            MaxConcurrency = Math.Clamp(concurrency, 1, 8);
    }

    /// <summary>Single-flight enqueue: returns false when a run is already active.</summary>
    public bool Enqueue(string deadHost, bool force = false, string? suggestedHost = null)
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
            _failureReasons.Clear();
            _activeRunCancellation = new CancellationTokenSource();
        }

        var queued = _requestChannel.Writer.TryWrite(new UrlMigrationRunRequest(runId, deadHost, force, suggestedHost));
        if (!queued)
        {
            lock (_statusLock)
            {
                if (_runId == runId)
                {
                    _isRunning = false;
                    _activeRunCancellation?.Dispose();
                    _activeRunCancellation = null;
                }
            }
        }

        return queued;
    }

    /// <summary>Signals cancellation for the current run, if one is active.</summary>
    public bool CancelActiveRun()
    {
        lock (_statusLock)
        {
            if (!_isRunning || _activeRunCancellation is null)
            {
                return false;
            }

            _activeRunCancellation.Cancel();
            return true;
        }
    }

    /// <summary>Forces the migration engine to reset to an idle state if desynchronized or stuck.</summary>
    public void ForceReset()
    {
        lock (_statusLock)
        {
            try
            {
                _activeRunCancellation?.Cancel();
                _activeRunCancellation?.Dispose();
            }
            catch
            {
                // Ignore disposal errors on forced reset
            }

            _activeRunCancellation = null;
            _runId = null;
            _isRunning = false;
            _totalFound = 0;
            _processed = 0;
            _resolved = 0;
            _unresolved = 0;
            _currentBookmarkTitle = null;
            _failureReasons.Clear();
            _errorMessage = "Migration engine was manually reset.";
        }
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
                TopFailureReason = _failureReasons.OrderByDescending(p => p.Value).ThenBy(p => p.Key).Select(p => p.Key).FirstOrDefault(),
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

                CancellationTokenSource? userCancellation;
                lock (_statusLock)
                {
                    userCancellation = (_runId == request.RunId && _isRunning) ? _activeRunCancellation : null;
                }

                if (userCancellation is null)
                {
                    // Stale or invalidated request from an earlier cancelled/reset run - ignore and do not alter running state.
                    continue;
                }

                var runTimeout = RunTimeout > TimeSpan.Zero ? RunTimeout : DefaultRunTimeout;
                using var timeoutCancellation = new CancellationTokenSource(runTimeout);
                using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken,
                    userCancellation.Token,
                    timeoutCancellation.Token);

                try
                {
                    await RunMigrationAsync(request, runCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested
                                                          && !stoppingToken.IsCancellationRequested
                                                          && !userCancellation.IsCancellationRequested)
                {
                    _logger.LogWarning("URL migration run {RunId} timed out after {Timeout}.", request.RunId, runTimeout);
                    SetRunError(request.RunId, $"URL migration timed out after {FormatTimeout(runTimeout)}.");
                }
                catch (OperationCanceledException) when (userCancellation.IsCancellationRequested
                                                          && !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("URL migration run {RunId} was canceled by the user.", request.RunId);
                    SetRunError(request.RunId, "URL migration canceled.");
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Host shutdown is expected and should not be presented as a migration failure.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "URL migration run {RunId} failed.", request.RunId);
                    SetRunError(request.RunId, "URL migration failed.");
                }
                finally
                {
                    lock (_statusLock)
                    {
                        if (_runId == request.RunId)
                        {
                            _isRunning = false;
                            _currentBookmarkTitle = null;
                            _activeRunCancellation?.Dispose();
                            _activeRunCancellation = null;
                        }
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

    private void SetRunError(Guid runId, string message)
    {
        lock (_statusLock)
        {
            if (_runId == runId)
            {
                _errorMessage = message;
            }
        }
    }

    private static string FormatTimeout(TimeSpan timeout)
    {
        if (timeout.TotalMinutes >= 1 && timeout.TotalMinutes == Math.Truncate(timeout.TotalMinutes))
        {
            return $"{timeout.TotalMinutes:0} {(timeout.TotalMinutes == 1 ? "minute" : "minutes")}";
        }

        return FormattableString.Invariant(
            $"{timeout.TotalSeconds:0.###} {(timeout.TotalSeconds == 1 ? "second" : "seconds")}");
    }

    private async Task RunMigrationAsync(UrlMigrationRunRequest request, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var livenessGuard = scope.ServiceProvider.GetRequiredService<IDomainLivenessGuard>();
        var extractionService = scope.ServiceProvider.GetRequiredService<ISeriesExtractionService>();
        var settingsService = scope.ServiceProvider.GetRequiredService<AiTaggingSettingsService>();

        var deadHost = request.DeadHost;

        var candidateBookmarks = await db.BookmarkNodes
            .Where(n => n.Type == NodeType.Bookmark && !n.IsDeleted && n.Url != null)
            .ToListAsync(ct).ConfigureAwait(false);

        var hostMatched = candidateBookmarks.Where(n => HostMatches(n.Url!, deadHost)).ToList();
        if (hostMatched.Count == 0)
        {
            lock (_statusLock)
            {
                _errorMessage = $"No bookmarks found on host \"{deadHost}\".";
            }
            return;
        }

        // Liveness sanity check runs against every host-matched bookmark, independent of the
        // re-run skip below, since it's judging the domain itself. Skipped when the user
        // manually named the host (Force) - that's them asserting it's dead already.
        if (!request.Force)
        {
            var isAlive = await livenessGuard.IsDomainAliveAsync(hostMatched.Select(m => m.Url!), ct).ConfigureAwait(false);
            if (isAlive)
            {
                lock (_statusLock)
                {
                    _errorMessage = LivenessAbortMessage;
                }
                return;
            }
        }

        // Keep usable proposals from earlier runs. Unresolved rows must remain retryable,
        // including those saved before a timeout; rejected URLs are excluded separately.
        // Only genuinely resolved rows are skipped. Reverted rows carry a non-null ProposedUrl
        // and non-Unresolved confidence, so a bare "not Rejected" filter would silently block
        // re-migrating a bookmark the user reverted back onto the dead host.
        var existingUsableProposals = await db.UrlMigrationProposals
            .Where(p => p.DeadHost == deadHost
                && (p.Status == "Pending" || p.Status == "Approved")
                && p.Confidence != "Unresolved")
            .Select(p => new { p.BookmarkId, p.RunId })
            .ToListAsync(ct).ConfigureAwait(false);
        var existingUsable = new HashSet<Guid>(existingUsableProposals.Select(p => p.BookmarkId));

        var toProcess = hostMatched.Where(m => !existingUsable.Contains(m.Id)).ToList();
        if (toProcess.Count == 0)
        {
            // Point status back at the run that actually owns these pending proposals, so the
            // client's Current run tab (which queries by _status.RunId) doesn't come up empty —
            // Enqueue already minted a fresh, still-empty RunId for this attempt.
            var priorRunId = existingUsableProposals.Select(p => p.RunId).FirstOrDefault();
            lock (_statusLock)
            {
                _runId = priorRunId;
                _isRunning = false;
                _activeRunCancellation?.Dispose();
                _activeRunCancellation = null;
                _errorMessage = $"All {hostMatched.Count} matching bookmark(s) already have a usable proposal for \"{deadHost}\" — check the Current run tab.";
            }
            return;
        }

        lock (_statusLock)
        {
            _totalFound = toProcess.Count;
        }

        var extractions = await ExtractBatchAsync(extractionService, toProcess, ct).ConfigureAwait(false);
        var aiSettings = await settingsService.GetAsync(ct).ConfigureAwait(false);

        // Rejected proposals mark a candidate URL as "already seen and declined" for that
        // bookmark, so a re-run for the same dead host doesn't just hand back the same top
        // search result the user already turned down.
        var bookmarkIds = toProcess.Select(m => m.Id).ToList();
        var rejectedByBookmark = (await db.UrlMigrationProposals
            .Where(p => bookmarkIds.Contains(p.BookmarkId) && p.Status == "Rejected" && p.ProposedUrl != null)
            .Select(p => new { p.BookmarkId, p.ProposedUrl })
            .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(p => p.BookmarkId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlySet<string>)g.Select(x => NormalizeUrlForComparison(x.ProposedUrl!)).ToHashSet(StringComparer.OrdinalIgnoreCase));

        // Tracks the host every bookmark's search should prefer. If the user named a target
        // domain up front (SuggestedHost), search is hard-restricted to it for the whole run.
        // Otherwise it's auto-learned from whatever host first resolved a series - manga/anime
        // aggregator sites that host one series usually host most others, so later bookmarks in
        // the same run should try landing back there before scattering across the open web.
        var userSuggestedHost = string.IsNullOrWhiteSpace(request.SuggestedHost) ? null : request.SuggestedHost.Trim();
        string? preferredHost = userSuggestedHost;
        var restrictToPreferredHost = userSuggestedHost != null;

        var searchRun = new SearchRunContext();
        using var saveGate = new SemaphoreSlim(1, 1);
        await Parallel.ForEachAsync(Enumerable.Range(0, toProcess.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(MaxConcurrency, 1, 8), CancellationToken = ct },
            async (i, token) =>
        {
            using var workerScope = _scopeFactory.CreateScope();
            var services = workerScope.ServiceProvider;
            var workerDb = services.GetRequiredService<AppDbContext>();
            var bookmark = toProcess[i];
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["RunId"] = request.RunId, ["BookmarkId"] = bookmark.Id
            });
            string? workerPreferredHost;
            lock (_statusLock)
            {
                if (_runId != request.RunId) return;
                _currentBookmarkTitle = bookmark.Title;
                workerPreferredHost = preferredHost;
            }
            var excludedUrls = rejectedByBookmark.TryGetValue(bookmark.Id, out var rejected) ? rejected : EmptyExcludedUrls;
            var proposal = await BuildProposalAsync(request.RunId, deadHost, bookmark, extractions[i],
                services.GetRequiredService<IAlternativeUrlSearchService>(),
                services.GetRequiredService<ICandidateVerificationService>(),
                services.GetRequiredService<IAnilistScheduleProvider>(),
                services.GetRequiredService<IWaybackEpisodeIdResolver>(),
                excludedUrls, workerPreferredHost, restrictToPreferredHost, workerDb, searchRun, token).ConfigureAwait(false);

            // SQLite writes and auto-approval transactions remain serialized; HTTP work overlaps.
            await saveGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Replace obsolete unresolved proposals only after a retry completed. Cancellation
                // before this transaction leaves the prior proposal available for another retry.
                await using (var transaction = await workerDb.Database.BeginTransactionAsync(token))
                {
                    await workerDb.UrlMigrationProposals.Where(p => p.BookmarkId == bookmark.Id && p.DeadHost == deadHost &&
                        p.Status == "Pending" && p.Confidence == "Unresolved").ExecuteDeleteAsync(token);
                    workerDb.UrlMigrationProposals.Add(proposal);
                    await workerDb.SaveChangesAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token);
                }

                lock (_statusLock)
                {
                    if (_runId == request.RunId)
                    {
                        _processed++;
                        if (proposal.Confidence == "Unresolved")
                        {
                            _unresolved++;
                            foreach (var reason in (proposal.Detail ?? "Unknown failure").Split("; ").Distinct())
                            {
                                var normalized = reason.Replace(" (skipped for this run)", "");
                                // Healthy stages render as "<n> results"; only real failures count.
                                if (SearchStage.IsResultCountLabel(normalized))
                                {
                                    continue;
                                }

                                _failureReasons[normalized] = _failureReasons.GetValueOrDefault(normalized) + 1;
                            }
                        }
                        else
                        {
                            _resolved++;
                            if (!restrictToPreferredHost && proposal.ProposedHost != null)
                                preferredHost = proposal.ProposedHost;
                        }
                    }
                }
                if (aiSettings.MigrationAutoApproveHigh && proposal.Confidence == "High")
                {
                    try { await services.GetRequiredService<UrlMigrationApprovalService>().ApproveAsync([proposal.Id], token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { _logger.LogWarning(ex, "Auto-approve failed for URL migration proposal {ProposalId}", proposal.Id); }
                }
            }
            finally { saveGate.Release(); }
        }).ConfigureAwait(false);
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

    private static readonly IReadOnlySet<string> EmptyExcludedUrls = new HashSet<string>();

    private async Task<UrlMigrationProposal> BuildProposalAsync(
        Guid runId,
        string deadHost,
        BookmarkNode bookmark,
        SeriesExtraction extraction,
        IAlternativeUrlSearchService searchService,
        ICandidateVerificationService verificationService,
        IAnilistScheduleProvider anilistProvider,
        IWaybackEpisodeIdResolver episodeIdResolver,
        IReadOnlySet<string> excludedUrls,
        string? preferredHost,
        bool restrictToPreferredHost,
        AppDbContext db,
        SearchRunContext searchRun,
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

        // Some target sites key their watch URLs by AniList ID directly (e.g. Miruro's
        // /watch/{aniListId}/{slug}) - when the user's chosen target is one of these, skip the
        // fuzzy web-search/title-match pipeline entirely and construct the URL straight from an
        // AniList title lookup, which is far more reliable than scraping search results.
        if (restrictToPreferredHost && preferredHost != null && AniListIdKeyedHosts.Contains(preferredHost))
        {
            var direct = await TryResolveViaAniListIdAsync(
                preferredHost, deadHost, bookmark, anilistProvider, episodeIdResolver, verificationService, extraction, ct).ConfigureAwait(false);
            if (direct != null)
            {
                proposal.ProposedUrl = direct.Candidate.Url;
                proposal.ProposedHost = TryGetHost(direct.Candidate.Url);
                if (direct.EpisodeNumber != null)
                {
                    proposal.ChapterNumber = direct.EpisodeNumber;
                    if (direct.EpisodeMappingSparse)
                    {
                        proposal.Confidence = "Medium";
                        proposal.Detail = $"Series matched, episode {direct.EpisodeNumber} guessed via Wayback episode-id mapping - Wayback history is sparse, mapping might be inaccurate.";
                    }
                    else
                    {
                        proposal.Confidence = "High";
                        proposal.Detail = $"Series and episode matched (AniList ID + Wayback episode-id mapping - episode {direct.EpisodeNumber}).";
                    }
                }
                else
                {
                    proposal.Confidence = "Medium";
                    proposal.Detail = "series page only (AniList ID match) - episode number could not be recovered.";
                }

                return proposal;
            }
        }

        var attempts = new List<CandidateAttempt>();
        var diagnosticStages = new List<SearchStage>();

        // Direct target-host rewrite: the user already named the replacement host, and aggregator
        // sites frequently keep the dead site's slug, so try "{target}/{slug}/chapter-{n}" and the
        // series page before spending a search. Only runs when the run has a user-chosen target.
        if (restrictToPreferredHost && preferredHost != null && !AniListIdKeyedHosts.Contains(preferredHost))
        {
            var rewritten = await TryDirectHostRewriteAsync(proposal, deadHost, preferredHost, bookmark.Id,
                extraction, verificationService, attempts, diagnosticStages, ct).ConfigureAwait(false);
            if (rewritten != null)
            {
                return rewritten;
            }
        }

        var combinedCandidates = new List<SearchCandidate>();

        // Query local library catalog for verified series source URLs (if compatible with host preferences)
        if (!string.IsNullOrWhiteSpace(extraction.SeriesName) && extraction.SeriesName.Length >= 3)
        {
            try
            {
                var catalogHits = await db.LibraryCatalogEntries
                    .AsNoTracking()
                    .Where(e => !string.IsNullOrEmpty(e.SourceUrl) &&
                               (EF.Functions.Like(e.Title, "%" + extraction.SeriesName + "%") ||
                                (e.AlternateTitles != null && EF.Functions.Like(e.AlternateTitles, "%" + extraction.SeriesName + "%"))))
                    .Take(5)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                foreach (var hit in catalogHits)
                {
                    if (string.IsNullOrWhiteSpace(hit.SourceUrl) || HostMatches(hit.SourceUrl, deadHost))
                    {
                        continue;
                    }

                    // If search is restricted to a preferred host, catalog URL must match that host
                    if (restrictToPreferredHost && preferredHost != null && !HostMatches(hit.SourceUrl, preferredHost))
                    {
                        continue;
                    }

                    combinedCandidates.Add(new SearchCandidate(hit.SourceUrl, hit.Title, $"Catalog match ({hit.Provider})"));

                    // Take at most 1 catalog match when unrestricted so web search candidates aren't starved
                    if (!restrictToPreferredHost)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Catalog lookup skipped for {Series}", extraction.SeriesName);
            }
        }

        IReadOnlyList<SearchCandidate> candidates;
        string searchDetail;
        // Names the provider(s) that returned candidates, stamped on an unverified proposal's
        // detail so the user knows where the blocked candidate came from. Falls back to a generic
        // label when only the local catalog supplied candidates (its own provider is in the snippet).
        var searchSource = "search";
        try
        {
            var outcome = await searchService.SearchWithDiagnosticsAsync(extraction, deadHost, searchRun, ct, preferredHost, restrictToPreferredHost).ConfigureAwait(false);
            candidates = outcome.Candidates;
            searchDetail = diagnosticStages.Count > 0
                ? string.Join("; ", diagnosticStages.Concat(outcome.Stages).Select(s => s.Detail))
                : outcome.Detail;
            var providers = outcome.Stages
                .Where(stage => stage.CandidateCount > 0)
                .Select(stage => stage.Provider)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (providers.Count > 0)
                searchSource = string.Join(", ", providers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search failed for bookmark {BookmarkId}; proposal will be Unresolved.", bookmark.Id);
            candidates = [];
            searchDetail = "Search: " + SearchRunContext.DescribeFailure(ex);
        }

        combinedCandidates.AddRange(candidates);

        if (excludedUrls.Count > 0)
        {
            combinedCandidates = combinedCandidates.Where(c => !excludedUrls.Contains(NormalizeUrlForComparison(c.Url))).ToList();
        }

        if (combinedCandidates.Count == 0)
        {
            proposal.Confidence = "Unresolved";
            proposal.Detail = BuildUnresolvedDetail(attempts, searchDetail, excludedUrls);
            return proposal;
        }

        SearchCandidate? bestSeriesMatch = null;
        VerificationResult? bestSeriesMatchResult = null;
        SearchCandidate? bestBlockedCandidate = null;
        VerificationResult? bestBlockedResult = null;
        var bestBlockedRank = 0;

        foreach (var candidate in combinedCandidates.Take(MaxCandidatesToVerify))
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
                attempts.Add(new CandidateAttempt(candidate.Url, "verification error"));
                continue;
            }

            _logger.LogInformation(
                "URL migration candidate {Url} for bookmark {BookmarkId}: reachable={Reachable} series={SeriesMatched} chapter={ChapterMatched} - {Reason}",
                candidate.Url, bookmark.Id, result.Reachable, result.SeriesMatched, result.ChapterMatched, result.Detail);

            if (result.Reachable && result.SeriesMatched)
            {
                bestSeriesMatch = candidate;
                bestSeriesMatchResult = result;
                break; // Stop at first candidate that passes (plan §6.4).
            }

            attempts.Add(new CandidateAttempt(candidate.Url, result.Detail));

            // A blocked candidate (Cloudflare/403/429) could still be the right page - the check
            // just couldn't see it. Keep the strongest-evidence blocked candidate as a fallback
            // when nothing verifies, instead of discarding it as if it were definitely wrong.
            if (result.Blocked)
            {
                var rank = ScoreBlockedEvidence(candidate, extraction);
                if (rank > bestBlockedRank)
                {
                    bestBlockedRank = rank;
                    bestBlockedCandidate = candidate;
                    bestBlockedResult = result;
                }
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

        // Nothing verified, but a blocked candidate carried real series evidence. Surface it as a
        // Medium "unverified" proposal for the user to eyeball rather than reporting Unresolved -
        // reader sites routinely sit behind Cloudflare, so "blocked" is not the same as "wrong".
        if (bestBlockedCandidate != null && bestBlockedResult != null)
        {
            proposal.ProposedUrl = bestBlockedCandidate.Url;
            proposal.ProposedHost = TryGetHost(bestBlockedCandidate.Url);
            proposal.Confidence = "Medium";
            proposal.Detail = BuildUnverifiedBlockedDetail(bestBlockedResult, bestBlockedCandidate, searchSource);
            return proposal;
        }

        proposal.Confidence = "Unresolved";
        proposal.Detail = BuildUnresolvedDetail(attempts, searchDetail, excludedUrls);
        return proposal;
    }

    /// <summary>
    /// Direct rewrite against a user-chosen target host: derive the series slug and chapter from the
    /// old URL and try <c>https://{target}/{slug}/chapter-{n}</c> then the series page, verifying
    /// with the normal HTTP verifier before any search. Returns a High proposal when the chapter
    /// URL verifies (series+chapter), a Medium one when only the series page verifies, and null to
    /// fall through to the catalog/search flow when neither does. Attempts are recorded for the
    /// per-candidate diagnostics.
    /// </summary>
    private async Task<UrlMigrationProposal?> TryDirectHostRewriteAsync(
        UrlMigrationProposal proposal,
        string deadHost,
        string preferredHost,
        Guid bookmarkId,
        SeriesExtraction extraction,
        ICandidateVerificationService verificationService,
        List<CandidateAttempt> attempts,
        List<SearchStage> diagnosticStages,
        CancellationToken ct)
    {
        if (!SeriesExtractionFallback.TryParseSeriesSlugAndChapter(proposal.OldUrl, out var slug, out var chapter) ||
            string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        var chapterNumber = !string.IsNullOrWhiteSpace(chapter) ? chapter : extraction.ChapterNumber;
        var candidates = new List<SearchCandidate>();
        if (!string.IsNullOrWhiteSpace(chapterNumber))
        {
            candidates.Add(new SearchCandidate($"https://{preferredHost}/{slug}/chapter-{chapterNumber}", null, "Direct rewrite"));
        }

        candidates.Add(new SearchCandidate($"https://{preferredHost}/{slug}", null, "Direct rewrite"));

        // Same SSRF/noise guard every other candidate passes.
        candidates = SearchCandidateFilter.Filter(candidates, deadHost, maxResults: candidates.Count).ToList();
        if (candidates.Count == 0)
        {
            diagnosticStages.Add(new SearchStage("Direct rewrite", 0, "candidates filtered"));
            return null;
        }

        SearchCandidate? seriesPageCandidate = null;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var result = await TryVerifyAsync(candidate, extraction, verificationService, ct).ConfigureAwait(false);
            if (result is null)
            {
                attempts.Add(new CandidateAttempt(candidate.Url, "verification error"));
                continue;
            }

            _logger.LogInformation(
                "URL migration candidate {Url} for bookmark {BookmarkId}: reachable={Reachable} series={SeriesMatched} chapter={ChapterMatched} - {Reason}",
                candidate.Url, bookmarkId, result.Reachable, result.SeriesMatched, result.ChapterMatched, result.Detail);

            if (result.Reachable && result.SeriesMatched && result.ChapterMatched)
            {
                proposal.ProposedUrl = candidate.Url;
                proposal.ProposedHost = TryGetHost(candidate.Url);
                proposal.Confidence = "High";
                proposal.Detail = "Series and chapter matched (direct slug rewrite to target host).";
                return proposal;
            }

            if (result.Reachable && result.SeriesMatched)
            {
                seriesPageCandidate ??= candidate;
                continue;
            }

            attempts.Add(new CandidateAttempt(candidate.Url, result.Detail));
        }

        diagnosticStages.Add(new SearchStage("Direct rewrite", candidates.Count, seriesPageCandidate is null ? "no series match" : null));

        if (seriesPageCandidate != null)
        {
            proposal.ProposedUrl = seriesPageCandidate.Url;
            proposal.ProposedHost = TryGetHost(seriesPageCandidate.Url);
            proposal.Confidence = "Medium";
            proposal.Detail = "series page, chapter not confirmed (direct slug rewrite to target host).";
            return proposal;
        }

        return null;
    }

    /// <summary>
    /// Builds the Unresolved proposal detail: a compact, capped list of the candidate URLs that
    /// were tried and why each failed, falling back to the search stage summary (or the generic
    /// catch-all) only when nothing was tried. Never includes page bodies or credentials.
    /// </summary>
    private static string BuildUnresolvedDetail(
        IReadOnlyList<CandidateAttempt> attempts, string searchDetail, IReadOnlySet<string> excludedUrls)
    {
        if (attempts.Count > 0)
        {
            var detail = BuildTriedDetail(attempts);
            return excludedUrls.Count > 0 ? detail + "; previously rejected URL(s) excluded" : detail;
        }

        if (!string.IsNullOrWhiteSpace(searchDetail))
        {
            return excludedUrls.Count > 0 ? searchDetail + "; previously rejected URL(s) excluded" : searchDetail;
        }

        return excludedUrls.Count > 0
            ? "No new candidates found (previously rejected URL(s) excluded)."
            : "No candidates survived verification (unreachable, 403 Forbidden, or title did not match).";
    }

    private static string BuildTriedDetail(IReadOnlyList<CandidateAttempt> attempts)
    {
        var parts = attempts.Take(MaxDiagnosticEntries)
            .Select(a => $"{CompactUrlForDiagnostics(a.Url)} - {a.Reason}")
            .ToList();
        var text = "Tried: " + string.Join("; ", parts);
        if (attempts.Count > MaxDiagnosticEntries)
            text += "; ...";
        if (text.Length > MaxDiagnosticChars)
            text = text[..(MaxDiagnosticChars - 3)].TrimEnd() + "...";
        return text;
    }

    private static string CompactUrlForDiagnostics(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var path = uri.AbsolutePath.TrimEnd('/');
        return uri.Host + path;
    }

    /// <summary>
    /// Evidence rank for a blocked candidate: a chapter number in the URL outranks a series slug
    /// in the URL, which outranks a match only on the search result title. Returns 0 when the
    /// candidate has no series evidence at all, so an implausible blocked URL is not surfaced.
    /// Reuses the same title normalization/matching the verifier uses, rather than a second rule.
    /// </summary>
    private static int ScoreBlockedEvidence(SearchCandidate candidate, SeriesExtraction extraction)
    {
        if (string.IsNullOrWhiteSpace(extraction.SeriesName) ||
            !Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri))
        {
            return 0;
        }

        var threshold = HttpCandidateVerificationService.ResolveSeriesTokenThreshold(extraction);
        var pathAsText = Uri.UnescapeDataString(uri.AbsolutePath).Replace('-', ' ').Replace('_', ' ');
        if (!HttpCandidateVerificationService.IsSeriesMatch(extraction.SeriesName, pathAsText, threshold))
        {
            // Title-only evidence is still plausible, but it is the weakest signal.
            return HttpCandidateVerificationService.IsSeriesMatch(extraction.SeriesName, candidate.Title, threshold) ? 1 : 0;
        }

        return HttpCandidateVerificationService.IsChapterMatch(extraction.ChapterNumber, uri, null) ? 3 : 2;
    }

    private static string BuildUnverifiedBlockedDetail(
        VerificationResult result, SearchCandidate candidate, string searchSource)
    {
        var source = DescribeCandidateSource(candidate, searchSource);
        return $"Unverified: site blocked automated check ({result.Detail}). Found via {source}; review before approving.";
    }

    private static string DescribeCandidateSource(SearchCandidate candidate, string searchSource)
    {
        const string catalogPrefix = "Catalog match (";
        if (candidate.Snippet is { } snippet && snippet.StartsWith(catalogPrefix, StringComparison.Ordinal))
        {
            return snippet[catalogPrefix.Length..].TrimEnd(')');
        }

        return searchSource;
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

        // Guessed patterns (chapter-N, /N) don't cover every reader site's URL scheme (e.g. fanfox
        // uses /c035/1.html). Fall back to scraping the series page's own links and picking ones
        // whose path names the chapter number, so "series page only" doesn't become a dead end.
        return await TryDiscoveredChapterLinkAsync(seriesUrl, extraction, verificationService, ct).ConfigureAwait(false);
    }

    private static readonly Regex ChapterFormEscapeRegex = new(@"[.^$*+?()\[\]{}|\\]", RegexOptions.Compiled);

    private static async Task<string?> TryDiscoveredChapterLinkAsync(
        string seriesUrl, SeriesExtraction extraction, ICandidateVerificationService verificationService, CancellationToken ct)
    {
        var chapter = extraction.ChapterNumber;
        if (string.IsNullOrWhiteSpace(chapter))
            return null;

        IReadOnlyList<string> pageLinks;
        try
        {
            pageLinks = await verificationService.DiscoverPageLinksAsync(seriesUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        var forms = ChapterNumberForms(chapter);
        if (forms.Count == 0)
            return null;

        var candidates = pageLinks
            .Where(link => LinkNamesChapter(link, forms))
            .OrderBy(link => link.Length)
            .Take(8)
            .ToList();

        foreach (var link in candidates)
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

    /// <summary>Chapter number written as itself and common zero-padded widths (e.g. "35" also matches "035", "0035").</summary>
    private static List<string> ChapterNumberForms(string chapter)
    {
        var trimmed = chapter.Trim();
        if (!int.TryParse(trimmed, out var numeric))
            return [trimmed];

        return new List<string> { trimmed, numeric.ToString("D2"), numeric.ToString("D3"), numeric.ToString("D4") }
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool LinkNamesChapter(string link, IReadOnlyList<string> forms)
    {
        foreach (var form in forms)
        {
            var escaped = ChapterFormEscapeRegex.Replace(form, @"\$0");
            var pattern = new Regex($@"(?<!\d){escaped}(?!\d)", RegexOptions.IgnoreCase);
            if (pattern.IsMatch(link))
                return true;
        }

        return false;
    }

    /// <summary>Streaming sites whose watch URL embeds the AniList media id directly.</summary>
    private static readonly IReadOnlySet<string> AniListIdKeyedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "miruro.tv"
    };

    /// <summary>
    /// Dead hosts (aniwatch-family) whose episode query parameter is an opaque internal id with
    /// no relation to the real episode number - value is the query string key to read it from.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> OpaqueEpisodeIdHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["aniwatchtv.to"] = "ep",
        ["aniwatch.to"] = "ep",
        ["hianime.to"] = "ep",
        ["zoro.to"] = "ep",
    };

    private sealed record DirectResolution(SearchCandidate Candidate, string? EpisodeNumber, bool EpisodeMappingSparse = false);

    private async Task<DirectResolution?> TryResolveViaAniListIdAsync(
        string preferredHost,
        string deadHost,
        BookmarkNode bookmark,
        IAnilistScheduleProvider anilistProvider,
        IWaybackEpisodeIdResolver episodeIdResolver,
        ICandidateVerificationService verificationService,
        SeriesExtraction extraction,
        CancellationToken ct)
    {
        Dictionary<Guid, BestMatchLookupResult> matches;
        try
        {
            matches = await anilistProvider.FindBestMatchesBatchAsync(
                [(bookmark.Id, bookmark.Title, bookmark.Url)], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AniList ID lookup failed for bookmark {BookmarkId}", bookmark.Id);
            return null;
        }

        if (!matches.TryGetValue(bookmark.Id, out var result) || result.Unavailable || result.Match?.AniListId is not { } aniListId)
        {
            return null;
        }

        var episodeResolution = await TryResolveEpisodeNumberAsync(deadHost, bookmark.Url!, episodeIdResolver, ct).ConfigureAwait(false);
        var episodeNumber = episodeResolution?.EpisodeNumber;

        var title = result.Match.EnglishTitle ?? result.Match.RomajiTitle;
        var slug = Slugify(title);
        var seriesOnlyUrl = string.IsNullOrEmpty(slug)
            ? $"https://{preferredHost}/watch/{aniListId}"
            : $"https://{preferredHost}/watch/{aniListId}/{slug}";

        if (episodeNumber != null)
        {
            var deepLinkUrl = $"https://{preferredHost}/watch/{aniListId}?ep={episodeNumber}";
            var deepLinkCandidate = new SearchCandidate(deepLinkUrl, title, "AniList ID + Wayback episode-id mapping");
            var deepLinkVerification = await TryVerifyAsync(deepLinkCandidate, extraction, verificationService, ct).ConfigureAwait(false);

            if (deepLinkVerification is { Reachable: true, SeriesMatched: true })
            {
                return new DirectResolution(deepLinkCandidate, episodeNumber.ToString(), episodeResolution?.Sparse ?? false);
            }

            // Deep link 404'd, got rate-limited, or the episode-number guess put it on a page
            // whose title doesn't match - falling all the way back to Unresolved would throw away
            // a series match that IS good, just not at the guessed episode. Series page only
            // (Medium) beats nothing.
        }

        var seriesOnlyCandidate = new SearchCandidate(seriesOnlyUrl, title, "AniList ID match");
        var seriesOnlyVerification = await TryVerifyAsync(seriesOnlyCandidate, extraction, verificationService, ct).ConfigureAwait(false);

        return seriesOnlyVerification is { Reachable: true }
            ? new DirectResolution(seriesOnlyCandidate, null)
            : null;
    }

    private async Task<VerificationResult?> TryVerifyAsync(
        SearchCandidate candidate, SeriesExtraction extraction, ICandidateVerificationService verificationService, CancellationToken ct)
    {
        try
        {
            return await verificationService.VerifyAsync(candidate, extraction, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Verification failed for AniList-derived URL {Url}", candidate.Url);
            return null;
        }
    }

    /// <summary>
    /// Recovers the real episode number for a bookmark on a known opaque-episode-id host via
    /// <see cref="IWaybackEpisodeIdResolver"/>. Returns null when the host isn't registered, the
    /// bookmark URL has no episode query param, or resolution otherwise fails - callers fall back
    /// to a series-only (Medium confidence) URL in that case.
    /// </summary>
    private async Task<WaybackEpisodeResolution?> TryResolveEpisodeNumberAsync(
        string deadHost, string bookmarkUrl, IWaybackEpisodeIdResolver episodeIdResolver, CancellationToken ct)
    {
        if (!OpaqueEpisodeIdHosts.TryGetValue(deadHost, out var episodeParam) ||
            !Uri.TryCreate(bookmarkUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue(episodeParam, out var values))
        {
            return null;
        }

        var opaqueId = values.ToString();
        if (string.IsNullOrEmpty(opaqueId))
        {
            return null;
        }

        var pagePrefix = uri.GetLeftPart(UriPartial.Path);
        try
        {
            return await episodeIdResolver.ResolveEpisodeNumberAsync(pagePrefix, episodeParam, opaqueId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wayback episode-id resolution failed for {Url}", bookmarkUrl);
            return null;
        }
    }

    private static string Slugify(string value)
    {
        var lowered = value.ToLowerInvariant();
        var slug = SlugifyRegex().Replace(lowered, "-").Trim('-');
        return slug;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugifyRegex();

    private static string NormalizeUrlForComparison(string url) =>
        UrlComparisonNormalizer.Normalize(url);

    private static bool HostMatches(string url, string deadHost)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals(deadHost, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith("." + deadHost, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetHost(string? url) => Infrastructure.UrlHelpers.TryGetHost(url);

    private sealed record UrlMigrationRunRequest(Guid RunId, string DeadHost, bool Force = false, string? SuggestedHost = null);

    /// <summary>One verified candidate and why it did not become a proposal, for Unresolved diagnostics.</summary>
    private sealed record CandidateAttempt(string Url, string Reason);
}
