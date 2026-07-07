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
        }

        var queued = _requestChannel.Writer.TryWrite(new UrlMigrationRunRequest(runId, deadHost, force, suggestedHost));
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
        var anilistProvider = scope.ServiceProvider.GetRequiredService<IAnilistScheduleProvider>();
        var episodeIdResolver = scope.ServiceProvider.GetRequiredService<IWaybackEpisodeIdResolver>();

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

        // Re-run safe: skip bookmarks that already have a Pending proposal for this host from an
        // earlier (possibly interrupted) run.
        var alreadyPendingProposals = await db.UrlMigrationProposals
            .Where(p => p.DeadHost == deadHost && p.Status == "Pending")
            .Select(p => new { p.BookmarkId, p.RunId })
            .ToListAsync(ct).ConfigureAwait(false);
        var alreadyPending = new HashSet<Guid>(alreadyPendingProposals.Select(p => p.BookmarkId));

        var toProcess = hostMatched.Where(m => !alreadyPending.Contains(m.Id)).ToList();
        if (toProcess.Count == 0)
        {
            // Point status back at the run that actually owns these pending proposals, so the
            // client's Current run tab (which queries by _status.RunId) doesn't come up empty —
            // Enqueue already minted a fresh, still-empty RunId for this attempt.
            var priorRunId = alreadyPendingProposals.Select(p => p.RunId).FirstOrDefault();
            lock (_statusLock)
            {
                _runId = priorRunId;
                _errorMessage = $"All {hostMatched.Count} matching bookmark(s) already have a pending proposal for \"{deadHost}\" — check the Current run tab.";
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

        for (var i = 0; i < toProcess.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var bookmark = toProcess[i];
            var extraction = extractions[i];

            lock (_statusLock)
            {
                _currentBookmarkTitle = bookmark.Title;
            }

            var excludedUrls = rejectedByBookmark.TryGetValue(bookmark.Id, out var rejected)
                ? rejected
                : EmptyExcludedUrls;

            var proposal = await BuildProposalAsync(
                request.RunId, deadHost, bookmark, extraction, searchService, verificationService, anilistProvider, episodeIdResolver, excludedUrls, preferredHost, restrictToPreferredHost, ct).ConfigureAwait(false);

            if (!restrictToPreferredHost && proposal.ProposedHost != null && (proposal.Confidence == "High" || proposal.Confidence == "Medium"))
            {
                preferredHost = proposal.ProposedHost;
            }

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

        IReadOnlyList<SearchCandidate> candidates;
        try
        {
            candidates = await searchService.SearchAsync(extraction, deadHost, ct, preferredHost, restrictToPreferredHost).ConfigureAwait(false);
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

        if (excludedUrls.Count > 0)
        {
            candidates = candidates.Where(c => !excludedUrls.Contains(NormalizeUrlForComparison(c.Url))).ToList();
        }

        if (candidates.Count == 0)
        {
            proposal.Confidence = "Unresolved";
            proposal.Detail = excludedUrls.Count > 0
                ? "No new candidates found (previously rejected URL(s) excluded)."
                : "No search candidates found.";
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

            if (result.Reachable && result.ChapterMatched)
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
        url.TrimEnd('/');

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

    private sealed record UrlMigrationRunRequest(Guid RunId, string DeadHost, bool Force = false, string? SuggestedHost = null);
}
