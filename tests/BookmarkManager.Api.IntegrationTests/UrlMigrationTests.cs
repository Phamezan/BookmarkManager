using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class UrlMigrationTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Blocks until the test releases it, so the run stays "in progress" long enough to assert
    // a concurrent POST run returns 409.
    private sealed class GateSeriesExtractionService : ISeriesExtractionService
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SeriesExtraction> ExtractAsync(string title, string url, string? category, CancellationToken cancellationToken)
        {
            await Gate.Task.WaitAsync(cancellationToken);
            return new SeriesExtraction("Test Series", "10", "manga", UsedFallback: true);
        }
    }

    private sealed class StubSearchService : IAlternativeUrlSearchService
    {
        public Task<IReadOnlyList<SearchCandidate>> SearchAsync(SeriesExtraction extraction, string deadHost, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SearchCandidate>>(Array.Empty<SearchCandidate>());
    }

    // No candidates ever pass verification and the "old" URLs are always reported dead, so runs
    // in these tests deterministically resolve every bookmark to Unresolved without any real
    // network calls.
    private sealed class StubVerificationAndLivenessService : ICandidateVerificationService, IDomainLivenessGuard
    {
        public Task<VerificationResult> VerifyAsync(SearchCandidate candidate, SeriesExtraction extraction, CancellationToken ct)
            => Task.FromResult(new VerificationResult(false, false, false, "n/a"));

        public Task<bool> IsDomainAliveAsync(IEnumerable<string> urls, CancellationToken ct) => Task.FromResult(false);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateFactoryWithStubs(
        IntegrationTestWebApplicationFactory baseFactory, GateSeriesExtractionService gate)
    {
        return baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                ReplaceSingleton<ISeriesExtractionService>(services, gate);
                ReplaceSingleton<IAlternativeUrlSearchService>(services, new StubSearchService());
                var verification = new StubVerificationAndLivenessService();
                ReplaceSingleton<ICandidateVerificationService>(services, verification);
                ReplaceSingleton<IDomainLivenessGuard>(services, verification);
            });
        });
    }

    private static void ReplaceSingleton<TService>(IServiceCollection services, TService instance) where TService : class
    {
        var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor is not null)
        {
            services.Remove(descriptor);
        }

        services.AddSingleton(instance);
    }

    private static async Task<BookmarkNode> SeedBookmarkAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        string url,
        string? browserNodeId = null,
        int version = 1,
        string? previousUrl = null)
    {
        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Test Bookmark",
            Url = url,
            PreviousUrl = previousUrl,
            Type = NodeType.Bookmark,
            SyncState = SyncState.Synced,
            Version = version,
            UpdatedAt = DateTime.UtcNow,
            BrowserNodeId = browserNodeId
        };

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.BookmarkNodes.Add(bookmark);
        await db.SaveChangesAsync();
        return bookmark;
    }

    private static async Task<UrlMigrationProposal> SeedProposalAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory,
        Guid bookmarkId,
        string deadHost,
        string? proposedUrl,
        string status = "Pending",
        string? proposedHost = null)
    {
        var proposal = new UrlMigrationProposal
        {
            Id = Guid.NewGuid(),
            RunId = Guid.NewGuid(),
            BookmarkId = bookmarkId,
            DeadHost = deadHost,
            OldUrl = $"https://{deadHost}/series/chapter-10",
            ProposedUrl = proposedUrl,
            ProposedHost = proposedHost,
            SeriesName = "Test Series",
            ChapterNumber = "10",
            Confidence = proposedUrl == null ? "Unresolved" : "High",
            Status = status,
            CreatedAt = DateTime.UtcNow
        };

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.UrlMigrationProposals.Add(proposal);
        await db.SaveChangesAsync();
        return proposal;
    }

    private static async Task<BookmarkNode> ReloadBookmarkAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.BookmarkNodes.FindAsync(id))!;
    }

    private static async Task<UrlMigrationProposal> ReloadProposalAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.UrlMigrationProposals.FindAsync(id))!;
    }

    private static async Task<List<ExtensionCommandEntry>> ReloadCommandsAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, Guid bookmarkId, string commandType)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.ExtensionCommands.Where(c => c.BookmarkId == bookmarkId && c.CommandType == commandType).ToList();
    }

    [Fact]
    public async Task RunLifecycle_SecondConcurrentRunReturns409_AndStatusProgressesToCompletion()
    {
        var deadHost = "flamecomics.xyz";
        var gate = new GateSeriesExtractionService();
        var factory = CreateFactoryWithStubs(Factory, gate);

        var bookmark = await SeedBookmarkAsync(factory, $"https://{deadHost}/series/chapter-10");

        using var client = factory.CreateClient();

        var firstResponse = await client.PostAsJsonAsync("/api/bookmarks/url-migration/run", new StartUrlMigrationRequest(deadHost));
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        var firstStatus = await firstResponse.Content.ReadFromJsonAsync<UrlMigrationStatusDto>(JsonOptions);
        Assert.True(firstStatus!.IsRunning);

        var secondResponse = await client.PostAsJsonAsync("/api/bookmarks/url-migration/run", new StartUrlMigrationRequest(deadHost));
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);

        // Release the extraction stage so the run can complete.
        gate.Gate.SetResult();

        UrlMigrationStatusDto? finalStatus = null;
        for (var i = 0; i < 100; i++)
        {
            finalStatus = await client.GetFromJsonAsync<UrlMigrationStatusDto>("/api/bookmarks/url-migration/status", JsonOptions);
            if (finalStatus is { IsRunning: false })
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.NotNull(finalStatus);
        Assert.False(finalStatus!.IsRunning);
        Assert.Equal(1, finalStatus.TotalFound);
        Assert.Equal(1, finalStatus.Processed);
        Assert.Equal(1, finalStatus.Unresolved);
        Assert.Equal(0, finalStatus.Resolved);

        var proposals = await client.GetFromJsonAsync<List<UrlMigrationProposalDto>>(
            $"/api/bookmarks/url-migration/proposals?runId={firstStatus.RunId}", JsonOptions);
        Assert.NotNull(proposals);
        var proposal = Assert.Single(proposals!);
        Assert.Equal(bookmark.Id, proposal.BookmarkId);
        Assert.Equal("Unresolved", proposal.Confidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a host!")]
    [InlineData("http://scheme.com")]
    [InlineData("has/slash.com")]
    public async Task StartUrlMigration_InvalidHost_ReturnsBadRequest(string deadHost)
    {
        using var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/bookmarks/url-migration/run", new StartUrlMigrationRequest(deadHost));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ApproveProposals_MoreThan500Ids_ReturnsBadRequest()
    {
        var ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList();

        using var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/bookmarks/url-migration/proposals/approve", new DecideProposalsRequest(ids));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RejectProposals_MoreThan500Ids_ReturnsBadRequest()
    {
        var ids = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList();

        using var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/bookmarks/url-migration/proposals/reject", new DecideProposalsRequest(ids));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Approve_WritesUrlAndPreviousUrl_AndExactlyOneUpdateCommand()
    {
        var oldUrl = "https://flamecomics.xyz/solo-leveling/chapter-112";
        var newUrl = "https://asuracomic.net/series/solo-leveling/chapter-112";
        var bookmark = await SeedBookmarkAsync(Factory, oldUrl, browserNodeId: "brave-node-1", version: 1);
        var proposal = await SeedProposalAsync(Factory, bookmark.Id, "flamecomics.xyz", newUrl, proposedHost: "asuracomic.net");

        using var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/bookmarks/url-migration/proposals/approve", new DecideProposalsRequest([proposal.Id]));
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DecideProposalsResponse>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(1, result!.Succeeded);
        Assert.Equal(0, result.Failed);

        var reloadedBookmark = await ReloadBookmarkAsync(Factory, bookmark.Id);
        Assert.Equal(newUrl, reloadedBookmark.Url);
        Assert.Equal(oldUrl, reloadedBookmark.PreviousUrl);
        Assert.Equal(2, reloadedBookmark.Version);
        Assert.Equal(SyncState.Pending, reloadedBookmark.SyncState);
        Assert.Contains("[URL Migrator]", reloadedBookmark.Notes);

        var commands = await ReloadCommandsAsync(Factory, bookmark.Id, "Update");
        var command = Assert.Single(commands);
        Assert.Equal(1, command.ExpectedVersion);
        Assert.Equal("brave-node-1", command.BrowserNodeId);

        var reloadedProposal = await ReloadProposalAsync(Factory, proposal.Id);
        Assert.Equal("Approved", reloadedProposal.Status);
        Assert.NotNull(reloadedProposal.DecidedAt);
    }

    [Fact]
    public async Task Approve_BookmarkWithoutBrowserNodeId_UpdatesDbOnlyWithNoCommand()
    {
        var oldUrl = "https://flamecomics.xyz/solo-leveling/chapter-112";
        var newUrl = "https://asuracomic.net/series/solo-leveling/chapter-112";
        var bookmark = await SeedBookmarkAsync(Factory, oldUrl, browserNodeId: null, version: 1);
        var proposal = await SeedProposalAsync(Factory, bookmark.Id, "flamecomics.xyz", newUrl);

        using var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/bookmarks/url-migration/proposals/approve", new DecideProposalsRequest([proposal.Id]));
        response.EnsureSuccessStatusCode();

        var reloadedBookmark = await ReloadBookmarkAsync(Factory, bookmark.Id);
        Assert.Equal(newUrl, reloadedBookmark.Url);
        Assert.Equal(oldUrl, reloadedBookmark.PreviousUrl);

        var commands = await ReloadCommandsAsync(Factory, bookmark.Id, "Update");
        Assert.Empty(commands);
    }

    [Fact]
    public async Task Reject_TouchesNothingOnTheBookmark()
    {
        var oldUrl = "https://flamecomics.xyz/solo-leveling/chapter-112";
        var newUrl = "https://asuracomic.net/series/solo-leveling/chapter-112";
        var bookmark = await SeedBookmarkAsync(Factory, oldUrl, browserNodeId: "brave-node-1", version: 1);
        var proposal = await SeedProposalAsync(Factory, bookmark.Id, "flamecomics.xyz", newUrl);

        using var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/bookmarks/url-migration/proposals/reject", new DecideProposalsRequest([proposal.Id]));
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<DecideProposalsResponse>(JsonOptions);
        Assert.Equal(1, result!.Succeeded);

        var reloadedBookmark = await ReloadBookmarkAsync(Factory, bookmark.Id);
        Assert.Equal(oldUrl, reloadedBookmark.Url);
        Assert.Null(reloadedBookmark.PreviousUrl);
        Assert.Equal(1, reloadedBookmark.Version);
        Assert.Null(reloadedBookmark.Notes);

        var commands = await ReloadCommandsAsync(Factory, bookmark.Id, "Update");
        Assert.Empty(commands);

        var reloadedProposal = await ReloadProposalAsync(Factory, proposal.Id);
        Assert.Equal("Rejected", reloadedProposal.Status);
    }

    [Fact]
    public async Task Revert_RestoresOriginalUrl_AndEnqueuesUpdateCommand()
    {
        var deadUrl = "https://flamecomics.xyz/solo-leveling/chapter-112";
        var migratedUrl = "https://asuracomic.net/series/solo-leveling/chapter-112";

        var bookmark = await SeedBookmarkAsync(
            Factory, migratedUrl, browserNodeId: "brave-node-1", version: 2, previousUrl: deadUrl);
        var proposal = await SeedProposalAsync(
            Factory, bookmark.Id, "flamecomics.xyz", migratedUrl, status: "Approved", proposedHost: "asuracomic.net");

        using var client = Factory.CreateClient();
        var response = await client.PostAsync($"/api/bookmarks/url-migration/proposals/{proposal.Id}/revert", null);
        response.EnsureSuccessStatusCode();

        var reloadedBookmark = await ReloadBookmarkAsync(Factory, bookmark.Id);
        Assert.Equal(deadUrl, reloadedBookmark.Url);
        Assert.Equal(migratedUrl, reloadedBookmark.PreviousUrl);
        Assert.Equal(3, reloadedBookmark.Version);

        var commands = await ReloadCommandsAsync(Factory, bookmark.Id, "Update");
        var command = Assert.Single(commands);
        Assert.Equal(2, command.ExpectedVersion);

        var reloadedProposal = await ReloadProposalAsync(Factory, proposal.Id);
        Assert.Equal("Reverted", reloadedProposal.Status);
    }

    [Fact]
    public async Task Approve_NonPendingProposal_FailsGracefullyWithoutCrashing()
    {
        var oldUrl = "https://flamecomics.xyz/solo-leveling/chapter-112";
        var newUrl = "https://asuracomic.net/series/solo-leveling/chapter-112";
        var bookmark = await SeedBookmarkAsync(Factory, oldUrl, browserNodeId: "brave-node-1", version: 1);
        var proposal = await SeedProposalAsync(Factory, bookmark.Id, "flamecomics.xyz", newUrl, status: "Approved");

        using var client = Factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/bookmarks/url-migration/proposals/approve", new DecideProposalsRequest([proposal.Id]));

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<DecideProposalsResponse>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(0, result!.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.NotEmpty(result.Errors);

        var reloadedBookmark = await ReloadBookmarkAsync(Factory, bookmark.Id);
        Assert.Equal(oldUrl, reloadedBookmark.Url);
    }
}
