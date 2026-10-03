using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Infrastructure;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Automatic target-host discovery for the URL Migrator. Samples the dead host's bookmarks
/// (distinct series, spread across the collection), runs one series-level search each - SearXNG
/// first because it is free, Tavily only for a series where SearXNG surfaced no usable reader
/// host - then probes the top candidate hosts with plain HTTP (no search credits) against a small
/// set of URL templates built from the old slug/chapter. The result ranks hosts by how many
/// sampled series (and chapters) actually verify there, and reports the template that worked most
/// often so the migration run can reuse it.
/// </summary>
public sealed class TargetHostDiscoveryService
{
    public const int DefaultSampleSize = 20;
    public const int MinSampleSize = 5;
    public const int MaxSampleSize = 50;
    public const int MaxHosts = 5;
    public const int MaxProbeConcurrency = 6;

    /// <summary>How many series searches may be in flight at once. SearXNG's own pacing throttle
    /// still serializes its upstream calls, but overlapping a slow provider with the next series'
    /// Tavily fallback (and overlapping search with nothing else) keeps discovery within the
    /// overall budget instead of stacking 20 sequential search hops.</summary>
    public const int MaxSearchConcurrency = 3;

    public const int EarlyStopFailures = 8;

    /// <summary>Most chapter links from a series page that will be verified when learning a
    /// site's chapter URL shape. The series-page fetch itself is capped at one per series; this
    /// only bounds the follow-up probes so a link-heavy page cannot blow the run's budget.</summary>
    public const int MaxLearnedLinkProbes = 3;

    /// <summary>Overall wall-clock budget for one discovery. Discovery runs synchronously inside a
    /// request; the Blazor HttpClient gives up at 5 minutes, so this stops short of that and returns
    /// whatever has been verified so far rather than letting the caller see a transport timeout.</summary>
    public static readonly TimeSpan DiscoveryBudget = TimeSpan.FromMinutes(3);

    /// <summary>Detail note attached to a truncated result. Also lets the UI explain why fewer
    /// hosts/chapters are listed than usual.</summary>
    public const string PartialDetail = "partial: time budget reached";

    /// <summary>Per-probe verification budget. The verifier's own HTTP client also caps at 10s;
    /// this bounds a slow host so one target cannot stall the whole discovery.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Chapter URL templates in preference order; the first that verifies series+chapter
    /// wins for a series, and the most-used one across a host becomes its <c>BestPattern</c>.</summary>
    internal static readonly IReadOnlyList<string> ChapterPatterns =
    [
        "/{slug}/chapter-{n}",
        "/manga/{slug}/chapter-{n}/",
        "/series/{slug}/chapter-{n}/",
        "/read/{slug}/chapter-{n}/",
    ];

    /// <summary>Series-page templates, tried only when no chapter template verified.</summary>
    internal static readonly IReadOnlyList<string> SeriesPatterns =
    [
        "/{slug}",
        "/manga/{slug}/",
        "/series/{slug}",
    ];

    private readonly AppDbContext _db;
    private readonly ISeriesExtractionService _extraction;
    private readonly ISearxngSearchService _searxng;
    private readonly ITavilySearchService _tavily;
    private readonly AiTaggingSettingsService _settings;
    private readonly ICandidateVerificationService _verification;
    private readonly RejectedTargetHostStore _rejectedHosts;
    private readonly ILogger<TargetHostDiscoveryService> _logger;
    private readonly int _configuredSampleSize;
    private readonly TimeSpan _discoveryBudget;

    public TargetHostDiscoveryService(
        AppDbContext db,
        ISeriesExtractionService extraction,
        ISearxngSearchService searxng,
        ITavilySearchService tavily,
        AiTaggingSettingsService settings,
        ICandidateVerificationService verification,
        RejectedTargetHostStore rejectedHosts,
        ILogger<TargetHostDiscoveryService> logger,
        IConfiguration? configuration = null,
        TimeSpan? discoveryBudget = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _extraction = extraction ?? throw new ArgumentNullException(nameof(extraction));
        _searxng = searxng ?? throw new ArgumentNullException(nameof(searxng));
        _tavily = tavily ?? throw new ArgumentNullException(nameof(tavily));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _verification = verification ?? throw new ArgumentNullException(nameof(verification));
        _rejectedHosts = rejectedHosts ?? throw new ArgumentNullException(nameof(rejectedHosts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _discoveryBudget = discoveryBudget is { } custom && custom > TimeSpan.Zero ? custom : DiscoveryBudget;

        _configuredSampleSize = DefaultSampleSize;
        if (int.TryParse(configuration?["UrlMigration:DiscoverySampleSize"], out var configured) && configured > 0)
        {
            _configuredSampleSize = configured;
        }
    }

    public Task<TargetHostDiscoveryResultDto> DiscoverAsync(string deadHost, int? sampleSize, CancellationToken ct)
        => DiscoverAsync(deadHost, sampleSize, candidateHosts: null, ct);

    /// <summary>
    /// Runs discovery for <paramref name="deadHost"/>. When <paramref name="candidateHosts"/> is
    /// non-empty the search stage is skipped entirely (zero search credits) and only those hosts
    /// are probed; rejected hosts are allowed here because typing one is an explicit "retest this"
    /// instruction, and the row detail says so.
    /// </summary>
    public async Task<TargetHostDiscoveryResultDto> DiscoverAsync(
        string deadHost, int? sampleSize, IReadOnlyList<string>? candidateHosts, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deadHost);
        var resolvedSampleSize = Math.Clamp(sampleSize ?? _configuredSampleSize, MinSampleSize, MaxSampleSize);

        var bookmarks = await _db.BookmarkNodes
            .AsNoTracking()
            .Where(n => n.Type == NodeType.Bookmark && !n.IsDeleted && n.Url != null)
            .ToListAsync(ct).ConfigureAwait(false);

        var hostMatched = bookmarks.Where(n => HostMatches(n.Url!, deadHost)).ToList();
        var sampledBookmarks = SelectSample(hostMatched, resolvedSampleSize);
        if (sampledBookmarks.Count == 0)
        {
            return Empty(deadHost, $"No bookmarks on \"{deadHost}\" have a parseable series/chapter URL to sample.");
        }

        // A single wall-clock budget covers extraction + search + probing. It never throws: once it
        // expires, discovery returns the partial ranking it managed to compute.
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(_discoveryBudget);
        var workCt = budgetCts.Token;

        IReadOnlyList<SampledSeries> sample;
        try
        {
            sample = await BuildSampledSeriesAsync(sampledBookmarks, workCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TargetHostDiscoveryResultDto { DeadHost = deadHost, Detail = PartialDetail };
        }

        var explicitHosts = NormalizeCandidateHosts(candidateHosts);
        var rejected = await LoadRejectedHostsAsync(ct).ConfigureAwait(false);

        var discovery = explicitHosts.Count > 0
            ? BuildExplicitHostDiscovery(explicitHosts, sample)
            : await DiscoverHostsAsync(deadHost, sample, rejected, workCt, ct).ConfigureAwait(false);

        var suggestions = new List<TargetHostSuggestionDto>();
        if (discovery.Tallies.Count > 0)
        {
            var topHosts = discovery.Tallies.Values
                .OrderByDescending(t => t.SeriesSeen.Count)
                .ThenBy(t => t.Host, StringComparer.Ordinal)
                .Take(MaxHosts)
                .ToList();

            var probe = await ProbeHostsAsync(
                deadHost, topHosts, sample, discovery.SearchUrls, rejected, explicitHosts.Count > 0, workCt, ct)
                .ConfigureAwait(false);
            suggestions = probe.Suggestions;
            discovery = discovery with { BudgetReached = discovery.BudgetReached || probe.BudgetReached };
        }

        suggestions = suggestions
            .OrderByDescending(s => s.ChaptersFound)
            .ThenByDescending(s => s.SeriesFound)
            .ThenBy(s => s.Host, StringComparer.Ordinal)
            .Take(MaxHosts)
            .ToList();

        string? detail;
        if (discovery.BudgetReached)
        {
            detail = PartialDetail;
        }
        else if (suggestions.Count == 0)
        {
            detail = discovery.Tallies.Count == 0
                ? "No candidate hosts found for the sampled series."
                : "No candidate host verified enough sampled series to suggest a target.";
        }
        else
        {
            detail = null;
        }

        return new TargetHostDiscoveryResultDto
        {
            DeadHost = deadHost,
            SampleSize = sample.Count,
            SearchCreditsUsed = discovery.TavilyCalls,
            Suggestions = suggestions,
            Detail = detail
        };
    }

    /// <summary>Normalizes a caller-supplied candidate host list to distinct bare hosts, in order.</summary>
    internal static IReadOnlyList<string> NormalizeCandidateHosts(IReadOnlyList<string>? candidateHosts)
    {
        if (candidateHosts is null || candidateHosts.Count == 0)
            return [];

        var result = new List<string>(candidateHosts.Count);
        foreach (var raw in candidateHosts)
        {
            var normalized = RejectedTargetHostStore.Normalize(raw);
            if (string.IsNullOrEmpty(normalized))
                continue;
            if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                result.Add(normalized);
        }

        return result;
    }

    private static HostDiscovery BuildExplicitHostDiscovery(
        IReadOnlyList<string> hosts, IReadOnlyList<SampledSeries> sample)
    {
        // No search stage: every explicitly named host is tallied against every sampled series so
        // it survives the top-5 selection and gets probed (explicit hosts numbered >5 are dropped
        // by the same Take(MaxHosts) the search path uses).
        var tallies = new Dictionary<string, HostTally>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in hosts)
        {
            var tally = new HostTally(host);
            foreach (var series in sample)
            {
                tally.SeriesSeen.Add(series.Slug);
            }

            tallies[host] = tally;
        }

        return new HostDiscovery(tallies, new Dictionary<(string Host, string Slug), List<string>>(), 0, BudgetReached: false);
    }

    private async Task<IReadOnlySet<string>> LoadRejectedHostsAsync(CancellationToken ct)
    {
        try
        {
            var hosts = await _rejectedHosts.GetHostsAsync(ct).ConfigureAwait(false);
            return new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load rejected target hosts; continuing without them.");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static TargetHostDiscoveryResultDto Empty(string deadHost, string detail) => new()
    {
        DeadHost = deadHost,
        SampleSize = 0,
        SearchCreditsUsed = 0,
        Detail = detail
    };

    /// <summary>
    /// Picks up to <paramref name="sampleSize"/> bookmarks whose URL parses to a slug+chapter,
    /// one per distinct slug (same series bookmarked at many chapters collapses to one), spread
    /// evenly across the whole list rather than taking the first N (which would cluster around one
    /// folder/series order and miss the rest of the collection). Pure and synchronous so sampling
    /// is directly testable.
    /// </summary>
    internal static IReadOnlyList<SampledBookmark> SelectSample(IReadOnlyList<BookmarkNode> bookmarks, int sampleSize)
    {
        var size = Math.Clamp(sampleSize, MinSampleSize, MaxSampleSize);

        var parsed = new List<SampledBookmark>();
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bookmark in bookmarks)
        {
            if (string.IsNullOrWhiteSpace(bookmark.Url) ||
                !SeriesExtractionFallback.TryParseSeriesSlugAndChapter(bookmark.Url, out var slug, out var chapter) ||
                string.IsNullOrWhiteSpace(slug) ||
                string.IsNullOrWhiteSpace(chapter))
            {
                continue;
            }

            if (seenSlugs.Add(slug))
            {
                parsed.Add(new SampledBookmark(slug, chapter, bookmark));
            }
        }

        if (parsed.Count <= size)
        {
            return parsed;
        }

        var picked = new List<SampledBookmark>(size);
        for (var i = 0; i < size; i++)
        {
            var index = (int)Math.Round(i * (parsed.Count - 1.0) / (size - 1), MidpointRounding.AwayFromZero);
            picked.Add(parsed[index]);
        }

        return picked;
    }

    private async Task<IReadOnlyList<SampledSeries>> BuildSampledSeriesAsync(
        IReadOnlyList<SampledBookmark> sample, CancellationToken ct)
    {
        IReadOnlyList<SeriesExtraction> extractions;
        if (_extraction is GroqSeriesExtractionService groq)
        {
            var items = sample
                .Select(s => new SeriesExtractionRequestItem(s.Bookmark.Title, s.Bookmark.Url!, s.Bookmark.Category))
                .ToList();
            extractions = await groq.ExtractBatchAsync(items, ct).ConfigureAwait(false);
        }
        else
        {
            var results = new List<SeriesExtraction>(sample.Count);
            foreach (var item in sample)
            {
                results.Add(await _extraction.ExtractAsync(item.Bookmark.Title, item.Bookmark.Url!, item.Bookmark.Category, ct).ConfigureAwait(false));
            }

            extractions = results;
        }

        var sampled = new List<SampledSeries>(sample.Count);
        for (var i = 0; i < sample.Count; i++)
        {
            var extraction = i < extractions.Count
                ? extractions[i]
                : SeriesExtractionFallback.Extract(sample[i].Bookmark.Title, sample[i].Bookmark.Url!, sample[i].Bookmark.Category);
            var seriesName = string.IsNullOrWhiteSpace(extraction.SeriesName)
                ? sample[i].Slug.Replace('-', ' ')
                : extraction.SeriesName;
            sampled.Add(new SampledSeries(sample[i].Slug, sample[i].Chapter, seriesName, extraction.MediaType));
        }

        return sampled;
    }

    private async Task<HostDiscovery> DiscoverHostsAsync(
        string deadHost, IReadOnlyList<SampledSeries> sample, IReadOnlySet<string> rejectedHosts,
        CancellationToken workCt, CancellationToken callerCt)
    {
        var tallies = new ConcurrentDictionary<string, HostTally>(StringComparer.OrdinalIgnoreCase);
        var searchUrls = new ConcurrentDictionary<(string Host, string Slug), List<string>>();
        var tavilyCalls = 0;
        var settings = await _settings.GetAsync(callerCt).ConfigureAwait(false);
        var tavilyConfigured = !string.IsNullOrWhiteSpace(settings.TavilyApiKey);

        // One circuit set for the whole discovery, matching a migration run: a provider that is
        // hard-failing for series after series should stop being retried. SearchRunContext is
        // thread-safe (concurrent dictionary + interlocked state).
        var run = new SearchRunContext();
        var budgetReached = false;

        try
        {
            await Parallel.ForEachAsync(sample,
                new ParallelOptions { MaxDegreeOfParallelism = MaxSearchConcurrency, CancellationToken = workCt },
                async (series, token) =>
                {
                    var extraction = new SeriesExtraction(series.SeriesName, null, series.MediaType, true);
                    var query = BuildDiscoveryQuery(series);

                    // Free provider first. Tavily is paid (1 credit/call), so it is only consulted
                    // when SearXNG yielded no usable reader host for this series.
                    var searxng = await _searxng.SearchWithDiagnosticsAsync(extraction, deadHost, run, token, queryOverride: query).ConfigureAwait(false);
                    var hosts = ExtractUsableHosts(searxng.Candidates, deadHost, rejectedHosts);
                    var viaTavily = false;

                    if (hosts.Count == 0 && tavilyConfigured)
                    {
                        viaTavily = true;
                        var tavily = await _tavily.SearchWithDiagnosticsAsync(extraction, deadHost, run, token, queryOverride: query).ConfigureAwait(false);
                        // A circuit-skipped call never reaches the API, so it spends no credit.
                        if (!tavily.Stages.Any(stage => stage.FailureReason?.Contains("skipped for this run", StringComparison.Ordinal) == true))
                        {
                            Interlocked.Increment(ref tavilyCalls);
                        }

                        hosts = ExtractUsableHosts(tavily.Candidates, deadHost, rejectedHosts);
                    }

                    foreach (var (host, urls) in hosts)
                    {
                        var tally = tallies.GetOrAdd(host, static h => new HostTally(h));
                        lock (tally)
                        {
                            tally.SeriesSeen.Add(series.Slug);
                            if (viaTavily)
                            {
                                tally.SeriesViaTavily.Add(series.Slug);
                            }
                        }

                        var list = searchUrls.GetOrAdd((host, series.Slug), static _ => []);
                        lock (list)
                        {
                            foreach (var url in urls)
                            {
                                if (!list.Contains(url, StringComparer.OrdinalIgnoreCase))
                                {
                                    list.Add(url);
                                }
                            }
                        }
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            // Budget expired, not the caller: keep the hosts found so far and let the probe stage
            // try them under the same (already-cancelled) token, which returns immediately.
            budgetReached = true;
        }

        return new HostDiscovery(new Dictionary<string, HostTally>(tallies, StringComparer.OrdinalIgnoreCase),
            new Dictionary<(string Host, string Slug), List<string>>(searchUrls), tavilyCalls, budgetReached);
    }

    private static string BuildDiscoveryQuery(SampledSeries series)
    {
        var mediaType = string.Equals(series.MediaType, "unknown", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" {series.MediaType}";
        return $"{series.SeriesName}{mediaType} read online".Trim();
    }

    /// <summary>
    /// Groups candidates by host after the shared SSRF/noise filter, so dead-host and
    /// private-address URLs can never reach the probe stage. Applies the filter even though the
    /// real providers already did, which keeps fakes/tests honest and defense-in-depth.
    /// </summary>
    private static Dictionary<string, List<string>> ExtractUsableHosts(
        IReadOnlyList<SearchCandidate> candidates, string deadHost, IReadOnlySet<string> rejectedHosts)
    {
        var filtered = SearchCandidateFilter.Filter(candidates, deadHost, maxResults: int.MaxValue);
        var hosts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in filtered)
        {
            if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri))
            {
                continue;
            }

            // A rejected host never enters the tally, so it is never probed and the next host
            // fills the freed top-5 slot.
            if (rejectedHosts.Contains(RejectedTargetHostStore.Normalize(uri.Host)))
            {
                continue;
            }

            if (!hosts.TryGetValue(uri.Host, out var urls))
            {
                urls = [];
                hosts[uri.Host] = urls;
            }

            urls.Add(candidate.Url);
        }

        return hosts;
    }

    private async Task<ProbeResult> ProbeHostsAsync(
        string deadHost,
        IReadOnlyList<HostTally> hosts,
        IReadOnlyList<SampledSeries> sample,
        Dictionary<(string Host, string Slug), List<string>> searchUrls,
        IReadOnlySet<string> rejectedHosts,
        bool explicitHosts,
        CancellationToken workCt,
        CancellationToken callerCt)
    {
        var results = new ConcurrentDictionary<string, ProbeTally>(StringComparer.OrdinalIgnoreCase);
        var budgetReached = false;

        try
        {
            await Parallel.ForEachAsync(hosts,
                new ParallelOptions { MaxDegreeOfParallelism = MaxProbeConcurrency, CancellationToken = workCt },
                async (host, token) =>
                {
                    var tally = new ProbeTally(host.Host);
                    var consecutiveFailures = 0;

                    foreach (var series in sample)
                    {
                        token.ThrowIfCancellationRequested();

                        // A host that misses many series in a row is the wrong site; stop spending
                        // probes on it and let the other candidates finish.
                        if (consecutiveFailures >= EarlyStopFailures)
                        {
                            tally.EarlyStopped = true;
                            break;
                        }

                        var urls = searchUrls.TryGetValue((host.Host, series.Slug), out var found) ? found : [];
                        var outcome = await ProbeSeriesAsync(host.Host, series, deadHost, urls, token).ConfigureAwait(false);

                        if (outcome.SeriesMatched)
                        {
                            tally.SeriesFound.Add(series.Slug);
                            consecutiveFailures = 0;
                        }
                        else
                        {
                            consecutiveFailures++;
                        }

                        if (outcome.ChapterMatched)
                        {
                            tally.ChaptersFound.Add(series.Slug);
                        }

                        if (outcome.ChapterPattern is { } pattern)
                        {
                            tally.PatternHits[pattern] = tally.PatternHits.GetValueOrDefault(pattern) + 1;
                        }
                    }

                    results[host.Host] = tally;
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            // Budget expired: keep whatever hosts finished and report a partial ranking.
            budgetReached = true;
        }

        var suggestions = new List<TargetHostSuggestionDto>();
        foreach (var tally in results.Values)
        {
            if (tally.SeriesFound.Count == 0)
            {
                continue;
            }

            var source = hosts.First(h => string.Equals(h.Host, tally.Host, StringComparison.OrdinalIgnoreCase));
            var retestedRejection = explicitHosts &&
                rejectedHosts.Contains(RejectedTargetHostStore.Normalize(tally.Host));
            suggestions.Add(new TargetHostSuggestionDto
            {
                Host = tally.Host,
                SeriesFound = tally.SeriesFound.Count,
                ChaptersFound = tally.ChaptersFound.Count,
                SampleSize = sample.Count,
                BestPattern = BestPattern(tally.PatternHits),
                SearchCreditsUsed = source.SeriesViaTavily.Count,
                Detail = BuildDetail(tally, retestedRejection)
            });
        }

        return new ProbeResult(suggestions, budgetReached);
    }

    private async Task<ProbeOutcome> ProbeSeriesAsync(
        string host, SampledSeries series, string deadHost, IReadOnlyList<string> searchUrls, CancellationToken ct)
    {
        string? seriesOnlyUrl = null;

        // URLs the search actually returned for this host+series are the strongest evidence.
        foreach (var url in searchUrls)
        {
            var result = await VerifyProbeAsync(url, series, deadHost, ct).ConfigureAwait(false);
            if (result is null)
            {
                continue;
            }

            if (result is { Reachable: true, SeriesMatched: true, ChapterMatched: true })
            {
                return new ProbeOutcome(true, true, GuessPatternFromUrl(url, series));
            }

            if (result is { Reachable: true, SeriesMatched: true })
            {
                seriesOnlyUrl ??= url;
            }
        }

        foreach (var pattern in ChapterPatterns)
        {
            var url = BuildProbeUrl(host, pattern, series);
            var result = await VerifyProbeAsync(url, series, deadHost, ct).ConfigureAwait(false);
            if (result is null)
            {
                continue;
            }

            if (result is { Reachable: true, SeriesMatched: true, ChapterMatched: true })
            {
                return new ProbeOutcome(true, true, pattern);
            }

            if (result is { Reachable: true, SeriesMatched: true })
            {
                seriesOnlyUrl ??= url;
            }
        }

        if (seriesOnlyUrl is null)
        {
            foreach (var pattern in SeriesPatterns)
            {
                var url = BuildProbeUrl(host, pattern, series);
                var result = await VerifyProbeAsync(url, series, deadHost, ct).ConfigureAwait(false);
                if (result is { Reachable: true, SeriesMatched: true })
                {
                    seriesOnlyUrl = url;
                    break;
                }
            }
        }

        if (seriesOnlyUrl is null)
        {
            return new ProbeOutcome(false, false, null);
        }

        // The series page verified but no guessed chapter shape did. Read that one page and pick a
        // same-host link whose path names the sampled chapter, so a site with a non-obvious scheme
        // still counts as a chapter match (and yields a usable pattern).
        var learned = await TryLearnChapterFromSeriesPageAsync(host, series, deadHost, seriesOnlyUrl, ct)
            .ConfigureAwait(false);
        return learned ?? new ProbeOutcome(true, false, null);
    }

    /// <summary>
    /// Fetches a verified series page once (through the verification service's capped HTTP client),
    /// keeps same-host links whose path chapter equals the sampled chapter (reusing
    /// <see cref="HttpCandidateVerificationService.IsChapterMatch"/> so a <c>/page/N</c> segment
    /// never counts), and verifies the best candidate. Returns null when nothing verifies.
    /// </summary>
    private async Task<ProbeOutcome?> TryLearnChapterFromSeriesPageAsync(
        string host, SampledSeries series, string deadHost, string seriesPageUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(seriesPageUrl, UriKind.Absolute, out var seriesUri))
        {
            return null;
        }

        IReadOnlyList<string> links;
        try
        {
            links = await _verification.DiscoverPageLinksAsync(seriesPageUrl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Series-page link discovery failed for {Url}", seriesPageUrl);
            return null;
        }

        var candidates = new List<(string Link, string? Pattern)>();
        foreach (var link in links)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var linkUri) ||
                (linkUri.Scheme != Uri.UriSchemeHttp && linkUri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            // Same host as the series page, and never a private/noise/dead address (the fake in
            // tests returns raw links, so this is the only guard for the SSRF surface here).
            if (!linkUri.Host.Equals(seriesUri.Host, StringComparison.OrdinalIgnoreCase) ||
                SearchCandidateFilter.Filter([new SearchCandidate(link, null, null)], deadHost, maxResults: 1).Count == 0)
            {
                continue;
            }

            if (!HttpCandidateVerificationService.IsChapterMatch(series.Chapter, linkUri, null))
            {
                continue;
            }

            candidates.Add((link, DerivePatternFromUrl(link, series)));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Prefer a link that names the slug (so a reusable {slug}/{n} pattern falls out), then the
        // shortest URL; a link with an opaque id still counts as a chapter but has no pattern.
        var ordered = candidates
            .OrderByDescending(c => c.Pattern is not null)
            .ThenBy(c => c.Link.Length)
            .Take(MaxLearnedLinkProbes)
            .ToList();

        foreach (var (link, pattern) in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var result = await VerifyProbeAsync(link, series, deadHost, ct).ConfigureAwait(false);
            if (result is { Reachable: true, SeriesMatched: true, ChapterMatched: true })
            {
                return new ProbeOutcome(true, true, pattern);
            }
        }

        return null;
    }

    /// <summary>True when a series page URL is on the same host as the candidate (case-insensitive).</summary>
    internal static bool SameHost(string a, string b)
        => Uri.TryCreate(a, UriKind.Absolute, out var ua) &&
           Uri.TryCreate(b, UriKind.Absolute, out var ub) &&
           ua.Host.Equals(ub.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a <c>{slug}</c>/<c>{n}</c> template from a picked chapter link when both the series
    /// slug and the chapter number appear literally in its path; otherwise null (the link has an
    /// id/hash instead of the slug).
    /// </summary>
    internal static string? DerivePatternFromUrl(string url, SampledSeries series)
    {
        if (string.IsNullOrWhiteSpace(series.Slug) || string.IsNullOrWhiteSpace(series.Chapter) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = uri.AbsolutePath;

        // Replace only the numeric token of a chapter-marker segment that equals the sampled
        // chapter, leaving the marker ("chapter-") in place.
        var replaced = ChapterSegmentTokenRegex.Replace(path, match =>
            ChapterValuesEqual(match.Groups["num"].Value, series.Chapter)
                ? match.Groups["marker"].Value + "{n}"
                : match.Value);

        if (!replaced.Contains("{n}", StringComparison.Ordinal) ||
            !replaced.Contains(series.Slug, StringComparison.Ordinal))
        {
            return null;
        }

        return replaced.Replace(series.Slug, "{slug}", StringComparison.Ordinal);
    }

    // Matches the numeric part of a chapter-marker path segment, bounded by path separators so it
    // cannot bleed into the slug. Captures all leading zeros so the whole token is replaceable.
    private static readonly Regex ChapterSegmentTokenRegex = new(
        @"(?<=/|^)(?<marker>(?:chapter|ch|episode|ep|c)[-_.]?)(?<num>\d+(?:\.\d+)?)(?=/|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool ChapterValuesEqual(string found, string expected)
    {
        if (decimal.TryParse(found, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var foundValue) &&
            decimal.TryParse(expected, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var expectedValue))
        {
            return foundValue == expectedValue;
        }

        return string.Equals(found.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<VerificationResult?> VerifyProbeAsync(
        string url, SampledSeries series, string deadHost, CancellationToken ct)
    {
        // Re-check every probe URL: a constructed pattern inherits a host from search, but the
        // path could still be shaped into something the filter rejects, and fakes bypass the
        // provider-side filter entirely.
        if (SearchCandidateFilter.Filter([new SearchCandidate(url, null, null)], deadHost, maxResults: 1).Count == 0)
        {
            return null;
        }

        var extraction = new SeriesExtraction(series.SeriesName, series.Chapter, series.MediaType, true);
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(ProbeTimeout);
        try
        {
            return await _verification.VerifyAsync(new SearchCandidate(url, null, null), extraction, probeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null; // probe budget exhausted for this URL
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Target-host probe failed for {Url}", url);
            return null;
        }
    }

    private static string BuildProbeUrl(string host, string pattern, SampledSeries series) =>
        $"https://{host}{BuildProbePath(pattern, series)}";

    private static string BuildProbePath(string pattern, SampledSeries series) =>
        pattern.Replace("{slug}", series.Slug).Replace("{n}", series.Chapter);

    private static string? GuessPatternFromUrl(string url, SampledSeries series)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return ChapterPatterns.FirstOrDefault(pattern =>
            string.Equals(BuildProbePath(pattern, series), uri.AbsolutePath, StringComparison.Ordinal));
    }

    /// <summary>
    /// Most-hit chapter template. Ties resolve to the earliest built-in template in preference
    /// order, then alphabetically for learned (non-built-in) templates.
    /// </summary>
    internal static string? BestPattern(IReadOnlyDictionary<string, int> patternHits)
    {
        string? best = null;
        var bestHits = 0;
        foreach (var (pattern, hits) in patternHits)
        {
            if (hits > bestHits ||
                (hits == bestHits && best is not null && ComparePatternPreference(pattern, best) < 0))
            {
                best = pattern;
                bestHits = hits;
            }
        }

        return best;
    }

    private static int ComparePatternPreference(string left, string right)
    {
        var leftRank = PatternPreference(left);
        var rightRank = PatternPreference(right);
        var byRank = leftRank.CompareTo(rightRank);
        return byRank != 0 ? byRank : string.CompareOrdinal(left, right);
    }

    private static int PatternPreference(string pattern)
    {
        for (var i = 0; i < ChapterPatterns.Count; i++)
        {
            if (string.Equals(ChapterPatterns[i], pattern, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return ChapterPatterns.Count;
    }

    private static string BuildDetail(ProbeTally tally, bool retestedRejection)
    {
        var detail = $"Verified {tally.SeriesFound.Count} series, {tally.ChaptersFound.Count} chapters.";
        if (tally.EarlyStopped)
        {
            detail += " Stopped probing early (most sampled series failed here).";
        }

        return retestedRejection
            ? detail + " Previously rejected — retested because you named it explicitly."
            : detail;
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

    internal sealed record SampledBookmark(string Slug, string Chapter, BookmarkNode Bookmark);

    internal sealed record SampledSeries(string Slug, string Chapter, string SeriesName, string MediaType);

    private sealed record HostDiscovery(
        Dictionary<string, HostTally> Tallies,
        Dictionary<(string Host, string Slug), List<string>> SearchUrls,
        int TavilyCalls,
        bool BudgetReached = false);

    private sealed record ProbeResult(List<TargetHostSuggestionDto> Suggestions, bool BudgetReached);

    private sealed class HostTally(string host)
    {
        public string Host { get; } = host;
        public HashSet<string> SeriesSeen { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SeriesViaTavily { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProbeTally(string host)
    {
        public string Host { get; } = host;
        public HashSet<string> SeriesFound { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ChaptersFound { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> PatternHits { get; } = new(StringComparer.Ordinal);
        public bool EarlyStopped { get; set; }
    }

    private sealed record ProbeOutcome(bool SeriesMatched, bool ChapterMatched, string? ChapterPattern);
}
