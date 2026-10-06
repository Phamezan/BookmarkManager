using System.Threading.Channels;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>
/// Orchestrates a Suwayomi import run: for each bookmark in the chosen folder subtree, search the
/// configured sources, score candidates, and create a <see cref="UrlMigrationProposal"/>; exact
/// (High) matches are auto-approved through the normal approval path. Modeled on
/// <see cref="UrlMigrationBackgroundJob"/> (single-flight enqueue, status snapshot under lock).
/// Does no library mutation during the search phase — only auto-approval does, and only via the
/// shared approval service.
/// </summary>
public sealed class SuwayomiImportBackgroundJob : BackgroundService
{
    public const string ServiceName = "SuwayomiImportBackgroundJob";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<SuwayomiOptions> _options;
    private readonly ILogger<SuwayomiImportBackgroundJob> _logger;
    private readonly Channel<SuwayomiImportRunRequest> _requestChannel = Channel.CreateUnbounded<SuwayomiImportRunRequest>();

    private readonly object _statusLock = new();
    private bool _isRunning;
    private Guid? _runId;
    private string _folderTitle = string.Empty;
    private int _totalFound;
    private int _processed;
    private int _matched;
    private int _autoApplied;
    private int _needsReview;
    private int _notFound;
    private string? _currentTitle;
    private string? _currentSource;
    private string? _errorMessage;
    private CancellationTokenSource? _activeRunCancellation;

    public SuwayomiImportBackgroundJob(
        IServiceScopeFactory scopeFactory,
        IOptions<SuwayomiOptions> options,
        ILogger<SuwayomiImportBackgroundJob> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    public bool IsRunning
    {
        get
        {
            lock (_statusLock)
            {
                return _isRunning;
            }
        }
    }

    public bool Enqueue(Guid folderId, string folderTitle)
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
            _folderTitle = folderTitle;
            _totalFound = 0;
            _processed = 0;
            _matched = 0;
            _autoApplied = 0;
            _needsReview = 0;
            _notFound = 0;
            _currentTitle = null;
            _currentSource = null;
            _errorMessage = null;
            _activeRunCancellation = new CancellationTokenSource();
        }

        var queued = _requestChannel.Writer.TryWrite(new SuwayomiImportRunRequest(runId, folderId, folderTitle));
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

    public SuwayomiImportStatusDto GetStatus()
    {
        lock (_statusLock)
        {
            return new SuwayomiImportStatusDto
            {
                IsRunning = _isRunning,
                RunId = _runId,
                FolderTitle = _folderTitle,
                TotalFound = _totalFound,
                Processed = _processed,
                Matched = _matched,
                AutoApplied = _autoApplied,
                NeedsReview = _needsReview,
                NotFound = _notFound,
                CurrentTitle = _currentTitle,
                CurrentSource = _currentSource,
                ErrorMessage = _errorMessage
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Suwayomi import background job started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            SuwayomiImportRunRequest request;
            try
            {
                request = await _requestChannel.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            CancellationTokenSource? userCancellation;
            lock (_statusLock)
            {
                userCancellation = (_runId == request.RunId && _isRunning) ? _activeRunCancellation : null;
            }

            if (userCancellation is null)
            {
                continue;
            }

            var timeout = TimeSpan.FromMinutes(Math.Max(1, _options.Value.RunTimeoutMinutes));
            using var timeoutCancellation = new CancellationTokenSource(timeout);
            using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, userCancellation.Token, timeoutCancellation.Token);

            try
            {
                await RunImportAsync(request, runCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested
                                                      && !stoppingToken.IsCancellationRequested
                                                      && !userCancellation.IsCancellationRequested)
            {
                _logger.LogWarning("Suwayomi import run {RunId} timed out.", request.RunId);
                SetRunError(request.RunId, "Suwayomi import timed out.");
            }
            catch (OperationCanceledException) when (userCancellation.IsCancellationRequested
                                                      && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Suwayomi import run {RunId} was canceled by the user.", request.RunId);
                SetRunError(request.RunId, "Suwayomi import canceled.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Suwayomi import run {RunId} failed.", request.RunId);
                SetRunError(request.RunId, "Suwayomi import failed.");
            }
            finally
            {
                lock (_statusLock)
                {
                    if (_runId == request.RunId)
                    {
                        _isRunning = false;
                        _currentTitle = null;
                        _currentSource = null;
                        _activeRunCancellation?.Dispose();
                        _activeRunCancellation = null;
                    }
                }
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

    private async Task RunImportAsync(SuwayomiImportRunRequest request, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var resolved = await SuwayomiScope.ResolveFolderAsync(db, request.FolderId, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            SetRunError(request.RunId, $"Folder {request.FolderId} was not found.");
            return;
        }

        var (_, folderIds) = resolved.Value;
        var bookmarks = await SuwayomiScope.BookmarksInFolder(db, folderIds)
            .Where(n => n.SuwayomiMangaId == null)
            .ToListAsync(ct).ConfigureAwait(false);

        var bookmarkIds = bookmarks.Select(b => b.Id).ToList();

        // Like the URL migrator: stale Unresolved results are retried fresh (a newly installed
        // extension may now have the series), everything else pending/approved is skipped.
        await db.UrlMigrationProposals
            .Where(p => p.IsSuwayomi && p.Status == "Pending" && p.Confidence == "Unresolved"
                && bookmarkIds.Contains(p.BookmarkId))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var pendingIds = (await db.UrlMigrationProposals
            .Where(p => p.IsSuwayomi
                && (p.Status == "Pending" || p.Status == "Approved")
                && bookmarkIds.Contains(p.BookmarkId))
            .Select(p => p.BookmarkId)
            .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();

        var toProcess = bookmarks.Where(b => !pendingIds.Contains(b.Id)).ToList();
        lock (_statusLock)
        {
            if (_runId == request.RunId)
            {
                _totalFound = toProcess.Count;
            }
        }

        if (toProcess.Count == 0)
        {
            return;
        }

        var client = scope.ServiceProvider.GetRequiredService<ISuwayomiClient>();
        var suwayomiStatus = await client.GetStatusAsync(ct).ConfigureAwait(false);
        var sourceLookup = BuildSourceLookup(suwayomiStatus.Sources, _options.Value.SourceOrder);

        var throttle = new SuwayomiSourceThrottle(_options.Value.ThrottleMillisecondsPerSource);
        using var saveGate = new SemaphoreSlim(1, 1);

        await Parallel.ForEachAsync(
            toProcess,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(_options.Value.MaxConcurrency, 1, 8), CancellationToken = ct },
            async (bookmark, token) =>
        {
            using var workerScope = _scopeFactory.CreateScope();
            var workerDb = workerScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var approval = workerScope.ServiceProvider.GetRequiredService<UrlMigrationApprovalService>();
            var workerClient = workerScope.ServiceProvider.GetRequiredService<ISuwayomiClient>();

            lock (_statusLock)
            {
                if (_runId != request.RunId)
                {
                    return;
                }

                _currentTitle = bookmark.Title;
                _currentSource = null;
            }

            var proposal = await BuildProposalAsync(
                request.RunId, bookmark, workerClient, sourceLookup, throttle, token).ConfigureAwait(false);

            await saveGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                workerDb.UrlMigrationProposals.Add(proposal);
                await workerDb.SaveChangesAsync(token).ConfigureAwait(false);

                var resolvedMatch = proposal.Confidence != "Unresolved";
                var autoApproved = false;
                if (proposal.Confidence == "High")
                {
                    try
                    {
                        var result = await approval.ApproveAsync([proposal.Id], token).ConfigureAwait(false);
                        autoApproved = result.Succeeded > 0;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Auto-approve failed for Suwayomi proposal {ProposalId}", proposal.Id);
                    }
                }

                lock (_statusLock)
                {
                    if (_runId != request.RunId)
                    {
                        return;
                    }

                    _processed++;
                    if (!resolvedMatch)
                    {
                        _notFound++;
                    }
                    else
                    {
                        _matched++;
                        if (autoApproved)
                        {
                            _autoApplied++;
                        }
                        else
                        {
                            _needsReview++;
                        }
                    }
                }
            }
            finally
            {
                saveGate.Release();
            }
        }).ConfigureAwait(false);
    }

    private async Task<UrlMigrationProposal> BuildProposalAsync(
        Guid runId,
        BookmarkNode bookmark,
        ISuwayomiClient client,
        IReadOnlyDictionary<string, string> sourceLookup,
        SuwayomiSourceThrottle throttle,
        CancellationToken ct)
    {
        var reference = SuwayomiTitleCleaner.Extract(bookmark.Title, bookmark.Url);
        var proposal = new UrlMigrationProposal
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            BookmarkId = bookmark.Id,
            DeadHost = Infrastructure.UrlHelpers.TryGetHost(bookmark.Url) ?? string.Empty,
            OldUrl = bookmark.Url ?? string.Empty,
            SeriesName = reference.SeriesName,
            ChapterNumber = reference.ChapterNumber,
            IsSuwayomi = true,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        if (string.IsNullOrWhiteSpace(reference.SeriesName))
        {
            proposal.Confidence = "Unresolved";
            proposal.Detail = "No exact match on your sources.";
            return proposal;
        }

        // An exact title match is only final when that source actually covers the bookmark
        // (has the bookmarked chapter, without big gaps): MangaDex in particular often lists a few
        // scattered chapters of licensed series. Otherwise keep searching later sources and fall
        // back to the first exact match as a reviewable Medium.
        SuwayomiCandidate? covering = null;
        SuwayomiCandidate? fallbackExact = null;
        SuwayomiCandidate? bestFuzzy = null;
        SuwayomiMangaAndChapters? fallbackDetails = null;
        SuwayomiMangaAndChapters? coveringDetails = null;
        var lastError = false;
        foreach (var sourceName in _options.Value.SourceOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (!sourceLookup.TryGetValue(sourceName, out var sourceId))
            {
                continue;
            }

            lock (_statusLock)
            {
                if (_runId == runId)
                {
                    _currentSource = sourceName;
                }
            }

            IReadOnlyList<SuwayomiManga> results;
            try
            {
                await throttle.WaitAsync(sourceName, ct).ConfigureAwait(false);
                results = await client.SearchAsync(sourceId, reference.SeriesName, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Suwayomi search failed for {Source}", sourceName);
                lastError = true;
                continue;
            }

            SuwayomiCandidate? exactHere = null;
            foreach (var manga in results)
            {
                if (SuwayomiMatchScorer.IsExact(reference.SeriesName, manga.Title))
                {
                    exactHere = new SuwayomiCandidate(sourceName, manga.Id, manga.Title, 1.0, true);
                    break;
                }

                var score = SuwayomiMatchScorer.Score(reference.SeriesName, manga.Title);
                if (bestFuzzy is null || score > bestFuzzy.Score)
                {
                    bestFuzzy = new SuwayomiCandidate(sourceName, manga.Id, manga.Title, score, false);
                }
            }

            if (exactHere is null)
            {
                continue;
            }

            var details = await TryGetDetailsAsync(client, exactHere.MangaId, ct).ConfigureAwait(false);
            if (details is not null && SuwayomiMatchScorer.Covers(details.Chapters, reference.ChapterNumber))
            {
                covering = exactHere;
                coveringDetails = details;
                break;
            }

            if (fallbackExact is null)
            {
                fallbackExact = exactHere;
                fallbackDetails = details;
            }
        }

        var best = covering ?? fallbackExact ?? bestFuzzy;
        if (best is null)
        {
            proposal.Confidence = "Unresolved";
            proposal.Detail = lastError ? "No exact match on your sources (some sources did not respond)." : "No exact match on your sources.";
            return proposal;
        }

        var chosenDetails = covering is not null ? coveringDetails
            : fallbackExact is not null ? fallbackDetails
            : await TryGetDetailsAsync(client, best.MangaId, ct).ConfigureAwait(false);
        var chapterCount = chosenDetails?.Chapters.Count ?? 0;
        double? latestValue = chapterCount > 0 ? chosenDetails!.Chapters.Max(c => c.ChapterNumber) : null;
        var latest = latestValue is { } lv ? FormatChapter(lv) : null;

        proposal.SuwayomiMangaId = best.MangaId;
        proposal.SourceName = best.SourceName;
        proposal.MatchedTitle = best.Title;
        proposal.SourceLatestChapter = latest;
        proposal.ProposedUrl = $"{_options.Value.PublicBaseUrl.TrimEnd('/')}/manga/{best.MangaId}";
        proposal.ProposedHost = Infrastructure.UrlHelpers.TryGetHost(proposal.ProposedUrl);
        proposal.Confidence = covering is not null ? "High"
            : fallbackExact is not null ? "Medium"
            : SuwayomiMatchScorer.Classify(best.Score, exact: false);
        proposal.Detail = BuildDetail(best.SourceName, reference.ChapterNumber, latest, latestValue, chapterCount);
        if (covering is null && fallbackExact is not null && chapterCount > 0 && latestValue is > 0 &&
            chapterCount < latestValue.Value * SuwayomiMatchScorer.MinChapterCoverage)
        {
            proposal.Detail += $" · only {chapterCount} chapters listed, likely missing chapters";
        }

        return proposal;
    }

    private async Task<SuwayomiMangaAndChapters?> TryGetDetailsAsync(ISuwayomiClient client, int mangaId, CancellationToken ct)
    {
        try
        {
            return await client.GetMangaAndChaptersAsync(mangaId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Suwayomi chapter fetch failed for manga {MangaId}", mangaId);
            return null;
        }
    }

    private static string BuildDetail(
        string sourceName, string? chapterNumber, string? latest, double? latestValue, int chapterCount)
    {
        var chapter = SuwayomiMatchScorer.ParseChapterNumber(chapterNumber);
        if (chapter is null)
        {
            return $"{sourceName} · {chapterCount} chapters · chapter unknown, nothing will be marked read";
        }

        if (latestValue is null || chapter.Value > latestValue.Value)
        {
            var endsAt = latest ?? "an unknown chapter";
            return $"Your bookmark is at ch {chapterNumber} but {sourceName} ends at {endsAt} — nothing will be marked read.";
        }

        var unread = Math.Max(0, chapterCount - chapter.Value);
        return $"{sourceName} · ch {chapterNumber} of {latest} · {unread} unread after import";
    }

    private static string FormatChapter(double value)
        => value == Math.Truncate(value)
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<string, string> BuildSourceLookup(
        IReadOnlyList<SuwayomiSource> sources, IReadOnlyList<string> sourceOrder)
    {
        var english = sources
            .Where(s => string.Equals(s.Lang, "en", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in sourceOrder)
        {
            var match = english.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                lookup[name] = match.Id;
            }
        }

        return lookup;
    }

    private sealed record SuwayomiImportRunRequest(Guid RunId, Guid FolderId, string FolderTitle);
    private sealed record SuwayomiCandidate(string SourceName, int MangaId, string Title, double Score, bool Exact);
}
