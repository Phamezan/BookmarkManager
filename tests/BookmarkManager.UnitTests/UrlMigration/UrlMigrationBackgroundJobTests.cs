using System.Data.Common;
using Microsoft.Extensions.Configuration;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.Contracts;
using BookmarkManager.UnitTests.UrlMigration.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests.UrlMigration;

public sealed class UrlMigrationBackgroundJobTests
{
    [Fact]
    public async Task BoundedConcurrency_UsesFourIndependentScopes_AndPersistsEveryResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var gate = new object();
        await using var harness = await Harness.Create(async ct =>
        {
            lock (gate)
            {
                active++;
                peak = Math.Max(peak, active);
                if (active == 4) entered.TrySetResult();
            }
            await release.Task.WaitAsync(ct);
            lock (gate) active--;
        });
        await harness.Seed(9);
        harness.Job.Enqueue("www.webtoon.xyz", force: true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, peak);
        }
        finally { release.TrySetResult(); }
        var status = await harness.WaitStopped();
        Assert.Null(status.ErrorMessage);
        Assert.Equal(9, status.Processed);
        Assert.Equal(4, peak);
        Assert.Equal(9, (await harness.Proposals()).Count);
        Assert.Equal("Groq: rate limited; DuckDuckGo: timeout; Yahoo: 0 results", (await harness.Proposals())[0].Detail);
        Assert.Equal("DuckDuckGo: timeout", status.TopFailureReason);
    }

    [Fact]
    public async Task Resume_RetriesUnresolvedAndRejected_PreservesResolvedAndApproved()
    {
        await using var harness = await Harness.Create(_ => Task.CompletedTask);
        var bookmarks = await harness.Seed(4);
        using (var scope = harness.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 4; i++) db.UrlMigrationProposals.Add(new()
            {
                Id = Guid.NewGuid(), RunId = Guid.NewGuid(), BookmarkId = bookmarks[i].Id,
                DeadHost = "www.webtoon.xyz", OldUrl = bookmarks[i].Url!,
                Status = i == 3 ? "Rejected" : i == 2 ? "Approved" : "Pending",
                Confidence = i == 0 ? "Unresolved" : "High", CreatedAt = DateTime.UtcNow,
                ProposedUrl = i == 0 ? null : "https://reader.example/chapter-51"
            });
            await db.SaveChangesAsync();
        }
        harness.Job.Enqueue("www.webtoon.xyz", force: true);
        var status = await harness.WaitStopped();
        Assert.Null(status.ErrorMessage);
        Assert.Equal(2, status.Processed);
        var proposals = await harness.Proposals();
        Assert.Equal(5, proposals.Count);
        Assert.Single(proposals, p => p.BookmarkId == bookmarks[0].Id);
        Assert.Equal(2, proposals.Count(p => p.RunId == status.RunId));
        Assert.Equal(2, proposals.Count(p => p.Confidence == "High" && p.Status != "Rejected"));
    }

    [Fact]
    public async Task Resume_ReprocessesRevertedBookmark_InsteadOfSkippingIt()
    {
        await using var harness = await Harness.Create(_ => Task.CompletedTask);
        var bookmarks = await harness.Seed(1);
        using (var scope = harness.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UrlMigrationProposals.Add(new()
            {
                Id = Guid.NewGuid(), RunId = Guid.NewGuid(), BookmarkId = bookmarks[0].Id,
                DeadHost = "www.webtoon.xyz", OldUrl = bookmarks[0].Url!,
                Status = "Reverted", Confidence = "High", CreatedAt = DateTime.UtcNow,
                ProposedUrl = "https://reader.example/chapter-51"
            });
            await db.SaveChangesAsync();
        }

        harness.Job.Enqueue("www.webtoon.xyz", force: true);
        var status = await harness.WaitStopped();

        Assert.Null(status.ErrorMessage);
        Assert.Equal(1, status.Processed);
        var proposals = await harness.Proposals();
        Assert.Contains(proposals, p => p.BookmarkId == bookmarks[0].Id && p.RunId == status.RunId);
    }

    [Fact]
    public async Task Timeout_PreservesPartialResults_AndNextRunRetriesUnresolved()
    {
        var calls = 0;
        var resume = false;
        await using var harness = await Harness.Create(async ct =>
        {
            if (Interlocked.Increment(ref calls) > 1 && !resume) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });
        await harness.Seed(2);
        harness.Job.MaxConcurrency = 1;
        harness.Job.RunTimeout = TimeSpan.FromMilliseconds(700);
        harness.Job.Enqueue("www.webtoon.xyz", force: true);
        var timedOut = await harness.WaitStopped();
        Assert.Contains("timed out", timedOut.ErrorMessage);
        Assert.Equal(1, timedOut.Processed);
        Assert.Single(await harness.Proposals());
        resume = true;
        harness.Job.RunTimeout = TimeSpan.FromSeconds(10);
        Assert.True(harness.Job.Enqueue("www.webtoon.xyz", force: true));
        var completed = await harness.WaitStopped();
        Assert.Null(completed.ErrorMessage);
        Assert.Equal(2, completed.Processed);
        Assert.Equal(2, (await harness.Proposals()).Count);
    }

    [Fact]
    public async Task ConcurrentResolvedResults_CanAutoApproveAfterProposalTransaction()
    {
        await using var harness = await Harness.Create(_ => Task.CompletedTask, resolved: true);
        await harness.Seed(6);
        harness.Job.Enqueue("www.webtoon.xyz", force: true);
        var status = await harness.WaitStopped();
        Assert.Null(status.ErrorMessage);
        Assert.Equal(6, status.Resolved);
        Assert.All(await harness.Proposals(), proposal => Assert.Equal("Approved", proposal.Status));
        using var scope = harness.Services.CreateScope();
        var bookmarks = await scope.ServiceProvider.GetRequiredService<AppDbContext>().BookmarkNodes.ToListAsync();
        Assert.All(bookmarks, bookmark => Assert.Equal("https://reader.example/chapter-51", bookmark.Url));
    }

    [Fact]
    public async Task ConcurrentCancelMidRun_NoDuplicateOrLostRows_ConsistentCounters()
    {
        const int total = 8;
        using var entered = new CountdownEvent(total);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = await Harness.Create(async ct =>
        {
            entered.Signal();
            await release.Task.WaitAsync(ct);
        }, resolved: true);
        await harness.Seed(total);
        harness.Job.MaxConcurrency = total;
        harness.Job.Enqueue("www.webtoon.xyz", force: true);

        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "expected all workers to start searching");
        release.SetResult();

        // Wait until at least one proposal actually committed, then cancel while other workers are
        // still at the save gate. Reads the in-memory status to avoid racing SQLite writers.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (harness.Job.GetStatus().Processed == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        harness.Job.CancelActiveRun();
        var status = await harness.WaitStopped();

        Assert.Equal("URL migration canceled.", status.ErrorMessage);
        // The save gate must keep DB transactions serialized despite MaxConcurrency = 8. Removing
        // the gate lets all 8 workers open their proposal transactions at once (peak 8 here).
        Assert.Equal(1, harness.TxInterceptor.PeakConcurrentTransactions);
        var proposals = await harness.Proposals();
        Assert.Equal(proposals.Select(p => p.BookmarkId).Distinct().Count(), proposals.Count);
        Assert.All(proposals.GroupBy(p => p.BookmarkId), group =>
            Assert.True(group.Count(p => p.Status == "Pending" && p.Confidence == "Unresolved") <= 1));
        Assert.Equal(status.Processed, status.Resolved + status.Unresolved);
        Assert.Equal(status.Resolved, proposals.Count(p => p.RunId == status.RunId && p.Confidence != "Unresolved"));
    }

    [Fact]
    public async Task TopFailureReason_ExcludesHealthyResultCountLabels()
    {
        await using var harness = await Harness.CreateWithSearch(new ResultCountOnlySearch());
        await harness.Seed(2);
        harness.Job.Enqueue("www.webtoon.xyz", force: true);
        var status = await harness.WaitStopped();

        Assert.Null(status.ErrorMessage);
        Assert.Equal(2, status.Unresolved);
        // Every stage returned a healthy "N results" label, so there is no failure reason to show.
        Assert.Null(status.TopFailureReason);
    }

    [Fact]
    public async Task BlockedPlausibleCandidate_ProducesMediumUnverifiedProposal()
    {
        const string blockedUrl = "https://reader.example/series/academy-player-0/chapter-51";
        var search = new CandidateSearch([new(blockedUrl, "Academy Player 0", null)]);
        var verification = new ScriptedVerification(_ =>
            new VerificationResult(false, false, false, "Cloudflare challenge", Blocked: true));
        await using var harness = await Harness.CreateWithSearch(search, verificationService: verification);
        await harness.Seed(1);

        harness.Job.Enqueue("www.webtoon.xyz", force: true);
        var status = await harness.WaitStopped();

        Assert.Null(status.ErrorMessage);
        Assert.Equal(1, status.Resolved);
        Assert.Equal(0, status.Unresolved);
        var proposal = Assert.Single(await harness.Proposals());
        Assert.Equal("Medium", proposal.Confidence);
        Assert.Equal("Pending", proposal.Status);
        Assert.Equal(blockedUrl, proposal.ProposedUrl);
        Assert.StartsWith("Unverified: site blocked automated check", proposal.Detail);
        Assert.Contains("Cloudflare challenge", proposal.Detail);
        Assert.Contains("review before approving", proposal.Detail);
    }

    [Fact]
    public async Task BlockedImplausibleCandidate_StaysUnresolved()
    {
        const string blockedUrl = "https://reader.example/series/some-other-series/chapter-99";
        var search = new CandidateSearch([new(blockedUrl, "Totally Different Series", null)]);
        var verification = new ScriptedVerification(_ =>
            new VerificationResult(false, false, false, "Access denied (HTTP 403)", Blocked: true));
        await using var harness = await Harness.CreateWithSearch(search, verificationService: verification);
        await harness.Seed(1);

        harness.Job.Enqueue("www.webtoon.xyz", force: true);
        var status = await harness.WaitStopped();

        Assert.Equal(1, status.Unresolved);
        var proposal = Assert.Single(await harness.Proposals());
        Assert.Equal("Unresolved", proposal.Confidence);
        Assert.Null(proposal.ProposedUrl);
    }

    [Fact]
    public async Task VerifiedCandidate_WinsOverBlockedPlausibleCandidate()
    {
        const string blockedUrl = "https://reader.example/series/academy-player-0/chapter-51";
        const string verifiedUrl = "https://goodreader.example/series/academy-player-0/chapter-51";
        var search = new CandidateSearch(
        [
            new(blockedUrl, "Academy Player 0", null),
            new(verifiedUrl, "Academy Player 0 Chapter 51", null),
        ]);
        var verification = new ScriptedVerification(candidate => candidate.Url == blockedUrl
            ? new VerificationResult(false, false, false, "Cloudflare challenge", Blocked: true)
            : new VerificationResult(true, true, true, "Series and chapter matched"));
        await using var harness = await Harness.CreateWithSearch(search, verificationService: verification);
        await harness.Seed(1);

        harness.Job.Enqueue("www.webtoon.xyz", force: true);
        var status = await harness.WaitStopped();

        Assert.Equal(1, status.Resolved);
        var proposal = Assert.Single(await harness.Proposals());
        Assert.Equal("High", proposal.Confidence);
        Assert.Equal(verifiedUrl, proposal.ProposedUrl);
        Assert.Equal("Series and chapter matched", proposal.Detail);
    }

    [Fact]
    public async Task UnverifiedBlockedProposal_IsNotAutoApproved()
    {
        const string blockedUrl = "https://reader.example/series/academy-player-0/chapter-51";
        var search = new CandidateSearch([new(blockedUrl, "Academy Player 0", null)]);
        var verification = new ScriptedVerification(_ =>
            new VerificationResult(false, false, false, "Cloudflare challenge", Blocked: true));
        await using var harness = await Harness.CreateWithSearch(search, autoApprove: true, verificationService: verification);
        var bookmarks = await harness.Seed(1);

        harness.Job.Enqueue("www.webtoon.xyz", force: true);
        await harness.WaitStopped();

        var proposal = Assert.Single(await harness.Proposals());
        Assert.Equal("Medium", proposal.Confidence);
        Assert.Equal("Pending", proposal.Status);

        using var scope = harness.Services.CreateScope();
        var bookmark = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .BookmarkNodes.SingleAsync(b => b.Id == bookmarks[0].Id);
        Assert.Equal("https://www.webtoon.xyz/read/academy-0/chapter-51/", bookmark.Url);
    }

    [Fact]
    public void Configuration_SetsRunTimeoutAndClampsConcurrency()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["UrlMigration:RunTimeoutMinutes"] = "45", ["UrlMigration:MaxConcurrency"] = "99" }).Build();
        using var job = new UrlMigrationBackgroundJob(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<UrlMigrationBackgroundJob>.Instance, configuration);
        Assert.Equal(TimeSpan.FromMinutes(45), job.RunTimeout);
        Assert.Equal(8, job.MaxConcurrency);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required ServiceProvider Services { get; init; }
        public required UrlMigrationBackgroundJob Job { get; init; }
        public required string DatabasePath { get; init; }
        public required TransactionConcurrencyInterceptor TxInterceptor { get; init; }
        public static Task<Harness> Create(Func<CancellationToken, Task> search, bool resolved = false)
            => CreateWithSearch(new Search(search, resolved), autoApprove: resolved);

        public static async Task<Harness> CreateWithSearch(
            IAlternativeUrlSearchService searchService,
            bool autoApprove = false,
            ICandidateVerificationService? verificationService = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"urlmig-unit-{Guid.NewGuid():N}.db");
            var services = new ServiceCollection();
            services.AddLogging();
            // Observes how many DB transactions are open at once. The save gate must keep this at 1;
            // removing it lets all concurrent workers start transactions together.
            var txInterceptor = new TransactionConcurrencyInterceptor();
            services.AddDbContext<AppDbContext>(options => options
                .UseSqlite($"Data Source={path};Pooling=False")
                .AddInterceptors(txInterceptor));
            services.AddSingleton<AiTaggingSettingsService>(new InMemoryAiTaggingSettingsService(new() { MigrationAutoApproveHigh = autoApprove }));
            services.AddSingleton<ISeriesExtractionService, Extraction>();
            services.AddScoped<IAlternativeUrlSearchService>(_ => searchService);
            services.AddSingleton<ICandidateVerificationService>(verificationService ?? new Verification());
            // Liveness always reports "dead" so the run proceeds; candidate verification is the scripted double.
            services.AddSingleton<IDomainLivenessGuard>(new Verification());
            services.AddSingleton<IAnilistScheduleProvider, Anilist>();
            services.AddSingleton<IWaybackEpisodeIdResolver, Wayback>();
            services.AddScoped<UrlMigrationApprovalService>();
            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            using (var scope = provider.CreateScope()) await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
            var job = new UrlMigrationBackgroundJob(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<UrlMigrationBackgroundJob>.Instance);
            await job.StartAsync(default);
            return new() { Services = provider, Job = job, DatabasePath = path, TxInterceptor = txInterceptor };
        }
        public async Task<List<BookmarkNode>> Seed(int count)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var nodes = Enumerable.Range(0, count).Select(i => new BookmarkNode
            {
                Id = Guid.NewGuid(), Title = $"Academy Player {i}", Url = $"https://www.webtoon.xyz/read/academy-{i}/chapter-51/",
                Type = NodeType.Bookmark, UpdatedAt = DateTime.UtcNow, SyncState = SyncState.Synced
            }).ToList();
            db.BookmarkNodes.AddRange(nodes);
            await db.SaveChangesAsync();
            return nodes;
        }
        public async Task<UrlMigrationStatusDto> WaitStopped()
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Job.GetStatus().IsRunning) await Task.Delay(10, limit.Token);
            return Job.GetStatus();
        }
        public async Task<List<UrlMigrationProposal>> Proposals()
        {
            using var scope = Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().UrlMigrationProposals.AsNoTracking().ToListAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await Job.StopAsync(default);
            Job.Dispose();
            await Services.DisposeAsync();
            File.Delete(DatabasePath);
        }
    }
    private sealed class Extraction : ISeriesExtractionService
    {
        public Task<SeriesExtraction> ExtractAsync(string title, string url, string? category, CancellationToken ct) => Task.FromResult(new SeriesExtraction(title, "51", "manga", true));
    }
    private sealed class Search(Func<CancellationToken, Task> action, bool resolved) : IAlternativeUrlSearchService
    {
        public Task<IReadOnlyList<SearchCandidate>> SearchAsync(SeriesExtraction extraction, string deadHost, CancellationToken ct, string? preferredHost = null, bool restrictToPreferredHost = false) => throw new NotSupportedException();
        public async Task<SearchOutcome<SearchCandidate>> SearchWithDiagnosticsAsync(SeriesExtraction extraction, string deadHost, SearchRunContext run, CancellationToken ct, string? preferredHost = null, bool restrictToPreferredHost = false)
        {
            await action(ct);
            return new(resolved ? [new("https://reader.example/chapter-51", null, null)] : [], [new("Groq", 0, "rate limited"), new("DuckDuckGo", 0, "timeout"), new("Yahoo", 0, null)]);
        }
    }
    private sealed class TransactionConcurrencyInterceptor : DbTransactionInterceptor
    {
        private int _active;
        private int _peak;
        private int _total;

        public int PeakConcurrentTransactions => Volatile.Read(ref _peak);
        public int TotalTransactions => Volatile.Read(ref _total);

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _total);
            var current = Interlocked.Increment(ref _active);
            if (current > _peak)
            {
                Volatile.Write(ref _peak, current);
            }

            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Decrement(ref _active);
            return Task.CompletedTask;
        }

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Decrement(ref _active);
            return Task.CompletedTask;
        }
    }

    private sealed class ResultCountOnlySearch : IAlternativeUrlSearchService
    {
        public Task<IReadOnlyList<SearchCandidate>> SearchAsync(SeriesExtraction extraction, string deadHost, CancellationToken ct, string? preferredHost = null, bool restrictToPreferredHost = false) => throw new NotSupportedException();
        public Task<SearchOutcome<SearchCandidate>> SearchWithDiagnosticsAsync(SeriesExtraction extraction, string deadHost, SearchRunContext run, CancellationToken ct, string? preferredHost = null, bool restrictToPreferredHost = false)
            => Task.FromResult(new SearchOutcome<SearchCandidate>([], [new("Yahoo", 0, null), new("DuckDuckGo", 0, null), new("Gemini", 3, null)]));
    }

    private sealed class Verification : ICandidateVerificationService, IDomainLivenessGuard
    {
        public Task<VerificationResult> VerifyAsync(SearchCandidate candidate, SeriesExtraction extraction, CancellationToken ct) => Task.FromResult(new VerificationResult(true, true, true, "matched"));
        public Task<IReadOnlyList<string>> DiscoverPageLinksAsync(string url, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IsDomainAliveAsync(IEnumerable<string> urls, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class CandidateSearch(IReadOnlyList<SearchCandidate> candidates, string provider = "Groq") : IAlternativeUrlSearchService
    {
        public Task<IReadOnlyList<SearchCandidate>> SearchAsync(SeriesExtraction extraction, string deadHost, CancellationToken ct, string? preferredHost = null, bool restrictToPreferredHost = false) => throw new NotSupportedException();

        public Task<SearchOutcome<SearchCandidate>> SearchWithDiagnosticsAsync(SeriesExtraction extraction, string deadHost, SearchRunContext run, CancellationToken ct, string? preferredHost = null, bool restrictToPreferredHost = false)
            => Task.FromResult(new SearchOutcome<SearchCandidate>(candidates, [new(provider, candidates.Count, null)]));
    }

    private sealed class ScriptedVerification(Func<SearchCandidate, VerificationResult> verify) : ICandidateVerificationService
    {
        public Task<VerificationResult> VerifyAsync(SearchCandidate candidate, SeriesExtraction extraction, CancellationToken ct)
            => Task.FromResult(verify(candidate));

        public Task<IReadOnlyList<string>> DiscoverPageLinksAsync(string seriesPageUrl, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }
    private sealed class Wayback : IWaybackEpisodeIdResolver
    {
        public Task<WaybackEpisodeResolution?> ResolveEpisodeNumberAsync(string url, string param, string id, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Anilist : IAnilistScheduleProvider
    {
        public bool IsAniListDegraded => false;
        public Task<List<AnimeMatchCandidateDto>> SearchCandidatesAsync(string title, string? url, CancellationToken ct) => throw new NotSupportedException();
        public Task<AnimeScheduleResult> GetAiringScheduleAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<Dictionary<int, AnimeScheduleResult>> GetAiringSchedulesBatchAsync(IReadOnlyList<int> ids, CancellationToken ct) => throw new NotSupportedException();
        public Task<Dictionary<Guid, BestMatchLookupResult>> FindBestMatchesBatchAsync(IReadOnlyList<(Guid Id, string Title, string? Url)> items, CancellationToken ct) => throw new NotSupportedException();
    }
}
