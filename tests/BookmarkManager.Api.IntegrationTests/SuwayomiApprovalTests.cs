using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class SuwayomiApprovalTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class FakeSuwayomiClient : ISuwayomiClient
    {
        public bool FailChapterFetch { get; set; }
        public string RealUrl { get; set; } = "https://asurascans.com/comics/nano-machine";
        public List<SuwayomiChapter> Chapters { get; set; } =
        [
            new SuwayomiChapter(4821, 0, false),
            new SuwayomiChapter(4822, 329, false),
            new SuwayomiChapter(4823, 330, false),
            new SuwayomiChapter(4824, 332, false)
        ];

        public bool AddedToLibrary { get; private set; }
        public bool RemovedFromLibrary { get; private set; }
        public IReadOnlyList<int> MarkedReadIds { get; private set; } = [];

        public Task<SuwayomiServerStatus> GetStatusAsync(CancellationToken ct)
            => Task.FromResult(new SuwayomiServerStatus("v2", [new SuwayomiSource("s1", "Asura Scans", "en")]));

        public Task<IReadOnlyList<SuwayomiManga>> SearchAsync(string sourceId, string query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SuwayomiManga>>([]);

        public Task<SuwayomiMangaAndChapters> GetMangaAndChaptersAsync(int mangaId, CancellationToken ct)
        {
            if (FailChapterFetch)
            {
                throw new SuwayomiException("Suwayomi could not be reached.");
            }

            return Task.FromResult(new SuwayomiMangaAndChapters(
                new SuwayomiManga(mangaId, "Nano Machine", false, RealUrl, null),
                Chapters));
        }

        public Task AddToLibraryAsync(int mangaId, CancellationToken ct)
        {
            AddedToLibrary = true;
            return Task.CompletedTask;
        }

        public Task RemoveFromLibraryAsync(int mangaId, CancellationToken ct)
        {
            RemovedFromLibrary = true;
            return Task.CompletedTask;
        }

        public Task MarkChaptersReadAsync(IReadOnlyList<int> chapterIds, CancellationToken ct)
        {
            MarkedReadIds = chapterIds;
            return Task.CompletedTask;
        }

        public Task<SuwayomiThumbnail> GetThumbnailAsync(int mangaId, CancellationToken ct)
            => throw new SuwayomiException("not used");

        public Task<IReadOnlyList<SuwayomiFilter>> GetSourceFiltersAsync(string sourceId, CancellationToken ct)
            => throw new SuwayomiException("not used");

        public Task<SuwayomiSourcePage> FetchSourceMangaAsync(
            string sourceId, IReadOnlyList<SuwayomiFilterChange> filters, int page, CancellationToken ct)
            => throw new SuwayomiException("not used");

        public Task<SuwayomiMangaDetails> GetMangaDetailsAsync(int mangaId, CancellationToken ct)
            => throw new SuwayomiException("not used");

        public Task<IReadOnlyList<SuwayomiLibraryManga>> GetLibraryAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SuwayomiLibraryManga>>([]);
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> CreateFactoryWithSuwayomi(
        IntegrationTestWebApplicationFactory baseFactory, FakeSuwayomiClient fake)
    {
        return baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ISuwayomiClient));
                if (descriptor is not null)
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<ISuwayomiClient>(fake);
            });
        });
    }

    private static async Task<BookmarkNode> SeedBookmarkAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, string url)
    {
        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Nano Machine - Chapter 330 - WEBTOON XYZ",
            Url = url,
            Type = NodeType.Bookmark,
            SyncState = SyncState.Synced,
            Version = 1,
            UpdatedAt = DateTime.UtcNow,
            BrowserNodeId = "brave-node-1"
        };

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.BookmarkNodes.Add(bookmark);
        await db.SaveChangesAsync();
        return bookmark;
    }

    private static async Task<UrlMigrationProposal> SeedSuwayomiProposalAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, Guid bookmarkId, int mangaId)
    {
        var proposal = new UrlMigrationProposal
        {
            Id = Guid.NewGuid(),
            RunId = Guid.NewGuid(),
            BookmarkId = bookmarkId,
            DeadHost = "webtoon.xyz",
            OldUrl = $"https://webtoon.xyz/read/nano-machine/chapter-330/",
            ProposedUrl = $"http://phamezan.capybara-pirarucu.ts.net:4567/manga/{mangaId}",
            ProposedHost = "phamezan.capybara-pirarucu.ts.net",
            SeriesName = "Nano Machine",
            ChapterNumber = "330",
            SuwayomiMangaId = mangaId,
            SourceName = "Asura Scans",
            MatchedTitle = "Nano Machine",
            SourceLatestChapter = "332",
            Confidence = "High",
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.UrlMigrationProposals.Add(proposal);
        await db.SaveChangesAsync();
        return proposal;
    }

    [Fact]
    public async Task ApprovingSuwayomiProposal_UpdatesBookmarkAndEnqueuesBraveUpdate()
    {
        var fake = new FakeSuwayomiClient();
        using var factory = CreateFactoryWithSuwayomi(Factory, fake);
        const string oldUrl = "https://webtoon.xyz/read/nano-machine/chapter-330/";
        var bookmark = await SeedBookmarkAsync(factory, oldUrl);
        var proposal = await SeedSuwayomiProposalAsync(factory, bookmark.Id, 2153);

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/bookmarks/url-migration/proposals/approve", new DecideProposalsRequest([proposal.Id]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DecideProposalsResponse>(JsonOptions);
        Assert.Equal(1, result!.Succeeded);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await db.BookmarkNodes.FindAsync(bookmark.Id);
        Assert.NotNull(reloaded);
        Assert.Equal(oldUrl, reloaded!.PreviousUrl);
        Assert.Equal("http://phamezan.capybara-pirarucu.ts.net:4567/manga/2153", reloaded.Url);
        Assert.Equal(2153, reloaded.SuwayomiMangaId);
        Assert.Equal(fake.RealUrl, reloaded.SourceUrl);
        Assert.Equal(330, reloaded.CurrentProgress);
        Assert.Equal("Nano Machine", reloaded.Title);

        Assert.True(fake.AddedToLibrary);
        Assert.Contains(4821, fake.MarkedReadIds);
        Assert.Contains(4823, fake.MarkedReadIds);
        Assert.DoesNotContain(4824, fake.MarkedReadIds);

        var commands = db.ExtensionCommands.Where(c => c.BookmarkId == bookmark.Id && c.CommandType == "Update").ToList();
        Assert.Single(commands);
    }

    [Fact]
    public async Task SuwayomiFailure_LeavesProposalPendingAndBookmarkUnchanged()
    {
        var fake = new FakeSuwayomiClient { FailChapterFetch = true };
        using var factory = CreateFactoryWithSuwayomi(Factory, fake);
        const string oldUrl = "https://webtoon.xyz/read/nano-machine/chapter-330/";
        var bookmark = await SeedBookmarkAsync(factory, oldUrl);
        var proposal = await SeedSuwayomiProposalAsync(factory, bookmark.Id, 2153);

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/bookmarks/url-migration/proposals/approve", new DecideProposalsRequest([proposal.Id]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<DecideProposalsResponse>(JsonOptions);
        Assert.Equal(0, result!.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Errors, e => e.Contains("Suwayomi", StringComparison.OrdinalIgnoreCase));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloadedProposal = await db.UrlMigrationProposals.FindAsync(proposal.Id);
        Assert.Equal("Pending", reloadedProposal!.Status);

        var reloadedBookmark = await db.BookmarkNodes.FindAsync(bookmark.Id);
        Assert.Equal(oldUrl, reloadedBookmark!.Url);
        Assert.Null(reloadedBookmark.SuwayomiMangaId);
        Assert.Empty(db.ExtensionCommands.Where(c => c.BookmarkId == bookmark.Id && c.CommandType == "Update"));
    }

    [Fact]
    public async Task Revert_KeepsSeriesInLibraryWhileAnotherBookmarkStillLinksIt()
    {
        var fake = new FakeSuwayomiClient();
        using var factory = CreateFactoryWithSuwayomi(Factory, fake);
        var first = await SeedBookmarkAsync(factory, "https://webtoon.xyz/read/nano-machine/chapter-330/");
        var second = await SeedBookmarkAsync(factory, "https://webtoon.xyz/read/nano-machine/chapter-200/");
        var firstProposal = await SeedSuwayomiProposalAsync(factory, first.Id, 2153);
        var secondProposal = await SeedSuwayomiProposalAsync(factory, second.Id, 2153);

        using var client = factory.CreateClient();
        var approve = await client.PostAsJsonAsync(
            "/api/bookmarks/url-migration/proposals/approve", new DecideProposalsRequest([firstProposal.Id, secondProposal.Id]));
        Assert.Equal(2, (await approve.Content.ReadFromJsonAsync<DecideProposalsResponse>(JsonOptions))!.Succeeded);

        var revertFirst = await client.PostAsync($"/api/bookmarks/url-migration/proposals/{firstProposal.Id}/revert", null);
        Assert.True(revertFirst.IsSuccessStatusCode);
        Assert.False(fake.RemovedFromLibrary);

        var revertSecond = await client.PostAsync($"/api/bookmarks/url-migration/proposals/{secondProposal.Id}/revert", null);
        Assert.True(revertSecond.IsSuccessStatusCode);
        Assert.True(fake.RemovedFromLibrary);
    }
}
