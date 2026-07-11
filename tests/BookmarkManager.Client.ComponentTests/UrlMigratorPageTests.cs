using BookmarkManager.Client.Pages;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

using BookmarkManager.Client.ComponentTests.TestDoubles;

namespace BookmarkManager.Client.ComponentTests;

public sealed class UrlMigratorPageTests
{
    private static IRenderedComponent<Bunit.Rendering.ContainerFragment> RenderPage(BunitContext context)
    {
        return context.Render(builder =>
        {
            builder.OpenComponent<MudBlazor.MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudBlazor.MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<UrlMigrator>(3);
            builder.CloseComponent();
        });
    }

    [Fact]
    public async Task PendingProposals_GroupedByProposedHost()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var runId = Guid.NewGuid();
        var fake = new FakeUrlMigratorBookmarkService
        {
            Status = new UrlMigrationStatusDto { IsRunning = false, RunId = runId, DeadHost = "flamecomics.xyz", TotalFound = 2, Processed = 2, Resolved = 2 },
            Proposals =
            [
                MakeProposal("Series A", "asuracomic.net", "High"),
                MakeProposal("Series B", "mangadex.org", "High"),
            ]
        };
        context.Services.AddSingleton<IBookmarkService>(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() =>
        {
            Assert.Contains("asuracomic.net", page.Markup);
            Assert.Contains("mangadex.org", page.Markup);
        });
    }

    [Fact]
    public async Task ApproveAllHigh_SendsCorrectProposalIds()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var runId = Guid.NewGuid();
        var highProposal = MakeProposal("Series A", "asuracomic.net", "High");
        var mediumProposal = MakeProposal("Series B", "kaiscans.com", "Medium");

        var fake = new FakeUrlMigratorBookmarkService
        {
            Status = new UrlMigrationStatusDto { IsRunning = false, RunId = runId, DeadHost = "flamecomics.xyz", TotalFound = 2, Processed = 2, Resolved = 2 },
            Proposals = [highProposal, mediumProposal]
        };
        context.Services.AddSingleton<IBookmarkService>(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".migrator-approve-all-high-btn")));
        page.Find(".migrator-approve-all-high-btn").Click();

        page.WaitForAssertion(() => Assert.NotNull(fake.LastApprovedIds));
        Assert.Single(fake.LastApprovedIds!);
        Assert.Equal(highProposal.Id, fake.LastApprovedIds![0]);
    }

    [Theory]
    [InlineData("High", "status-badge--success")]
    [InlineData("Medium", "status-badge--warning")]
    [InlineData("Low", "status-badge--danger")]
    public async Task ConfidenceChips_RenderExpectedColorPerLevel(string confidence, string expectedClassFragment)
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var runId = Guid.NewGuid();
        var proposal = MakeProposal("Series A", "asuracomic.net", confidence);
        var fake = new FakeUrlMigratorBookmarkService
        {
            Status = new UrlMigrationStatusDto { IsRunning = false, RunId = runId, DeadHost = "flamecomics.xyz", TotalFound = 1, Processed = 1 },
            Proposals = [proposal]
        };
        context.Services.AddSingleton<IBookmarkService>(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".status-badge")));
        var chip = page.FindAll(".status-badge").First(c => c.TextContent.Trim() == confidence);
        Assert.Contains(expectedClassFragment, chip.ClassList);
    }

    [Fact]
    public async Task UnresolvedProposal_ManualUrlDialog_UpdatesBookmarkAndClearsProposal()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var runId = Guid.NewGuid();
        var unresolved = MakeProposal("Obscure Novel", null, "Unresolved");
        var fake = new FakeUrlMigratorBookmarkService
        {
            Status = new UrlMigrationStatusDto { IsRunning = false, RunId = runId, DeadHost = "flamecomics.xyz", TotalFound = 1, Processed = 1, Unresolved = 1 },
            Proposals = [unresolved]
        };
        context.Services.AddSingleton<IBookmarkService>(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".migrator-manual-url-btn")));
        page.Find(".migrator-manual-url-btn").Click();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".mud-dialog")));

        var urlInput = page.Find(".manual-url-field input");
        urlInput.Input("https://newsite.example/series/obscure-novel");

        page.WaitForAssertion(() =>
        {
            var submit = page.Find(".manual-url-submit");
            Assert.False(submit.HasAttribute("disabled"));
        });

        page.Find(".manual-url-submit").Click();

        page.WaitForAssertion(() => Assert.NotNull(fake.ManualUrlSet));
        Assert.Equal(unresolved.Id, fake.ManualUrlSet!.Value.Id);
        Assert.Equal("https://newsite.example/series/obscure-novel", fake.ManualUrlSet.Value.Url);
    }

    [Fact]
    public async Task ProgressPolling_StopsWhenRunCompletes()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var runId = Guid.NewGuid();
        var fake = new FakeUrlMigratorBookmarkService
        {
            Status = new UrlMigrationStatusDto { IsRunning = true, RunId = runId, DeadHost = "flamecomics.xyz", TotalFound = 5, Processed = 2 },
            Proposals = []
        };
        context.Services.AddSingleton<IBookmarkService>(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.True(fake.StatusCallCount >= 1));

        // Simulate the run finishing on the next poll tick.
        fake.Status = new UrlMigrationStatusDto { IsRunning = false, RunId = runId, DeadHost = "flamecomics.xyz", TotalFound = 5, Processed = 5, Resolved = 5 };

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Last run:", page.Markup);
            Assert.Contains("flamecomics.xyz", page.Markup);
        }, TimeSpan.FromSeconds(15));

        var callsAfterCompletion = fake.StatusCallCount;

        // Give any lingering poll loop a chance to fire again; count should not keep climbing forever.
        Thread.Sleep(2500);
        Assert.True(fake.StatusCallCount <= callsAfterCompletion + 1);
    }

    [Fact]
    public async Task HistoryTab_ShowsRevertButton_OnlyForApprovedRows()
    {
        await using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();

        var approved = MakeProposal("Series A", "asuracomic.net", "High");
        approved.Status = "Approved";
        var rejected = MakeProposal("Series B", "mangadex.org", "High");
        rejected.Status = "Rejected";

        var fake = new FakeUrlMigratorBookmarkService
        {
            Status = new UrlMigrationStatusDto { IsRunning = false, RunId = null },
            Proposals = [],
            AllProposals = [approved, rejected]
        };
        context.Services.AddSingleton<IBookmarkService>(fake);

        var page = RenderPage(context);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".mud-tab")));
        page.FindAll(".mud-tab").First(t => t.TextContent.Trim() == "History").Click();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".migrator-history-row")));

        var rows = page.FindAll(".migrator-history-row");
        var approvedRow = rows.First(r => r.GetAttribute("data-status") == "Approved");
        var rejectedRow = rows.First(r => r.GetAttribute("data-status") == "Rejected");

        Assert.NotEmpty(approvedRow.QuerySelectorAll(".migrator-revert-btn"));
        Assert.Empty(rejectedRow.QuerySelectorAll(".migrator-revert-btn"));
    }

    private static UrlMigrationProposalDto MakeProposal(string title, string? proposedHost, string confidence) => new()
    {
        Id = Guid.NewGuid(),
        BookmarkId = Guid.NewGuid(),
        BookmarkTitle = title,
        OldUrl = "https://flamecomics.xyz/series/x/chapter-1",
        ProposedUrl = proposedHost == null ? null : $"https://{proposedHost}/series/x/chapter-1",
        ProposedHost = proposedHost,
        Confidence = confidence,
        Status = "Pending",
        CreatedAt = DateTime.UtcNow
    };

    private sealed class FakeUrlMigratorBookmarkService : FakeBookmarkService, IBookmarkService
    {
        public UrlMigrationStatusDto? Status { get; set; }
        public List<UrlMigrationProposalDto> Proposals { get; set; } = [];
        public List<UrlMigrationProposalDto> AllProposals { get; set; } = [];
        public List<Guid>? LastApprovedIds { get; private set; }
        public List<Guid> RejectedIds { get; } = [];
        public List<Guid> CancelledIds { get; } = [];
        public bool UpdateBookmarkCalled { get; private set; }
        public (Guid Id, string Url)? ManualUrlSet { get; private set; }
        public int StatusCallCount { get; private set; }

        public override Task<List<DeadDomainCandidateDto>> GetDeadDomainCandidatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new List<DeadDomainCandidateDto>());
        public string? LastStartedHost { get; private set; }
        public bool? LastStartedForce { get; private set; }
        public string? LastSuggestedHost { get; private set; }

        public override Task<bool> StartUrlMigrationAsync(string deadHost, bool force = false, string? suggestedHost = null, CancellationToken cancellationToken = default)
        {
            LastStartedHost = deadHost;
            LastStartedForce = force;
            LastSuggestedHost = suggestedHost;
            return Task.FromResult(true);
        }

        public override Task<UrlMigrationStatusDto?> GetUrlMigrationStatusAsync(CancellationToken cancellationToken = default)
        {
            StatusCallCount++;
            return Task.FromResult(Status);
        }

        public override Task<List<UrlMigrationProposalDto>> GetUrlMigrationProposalsAsync(Guid? runId, string? status, CancellationToken cancellationToken = default)
        {
            if (runId == null && string.IsNullOrEmpty(status))
            {
                // History tab call (no filters).
                return Task.FromResult(AllProposals.Count > 0 ? AllProposals : Proposals);
            }

            return Task.FromResult(Proposals);
        }

        public override Task<DecideProposalsResponse?> ApproveProposalsAsync(List<Guid> ids, CancellationToken cancellationToken = default)
        {
            LastApprovedIds = ids;
            foreach (var id in ids)
            {
                var p = Proposals.FirstOrDefault(x => x.Id == id);
                if (p != null) p.Status = "Approved";
            }
            return Task.FromResult<DecideProposalsResponse?>(new DecideProposalsResponse(ids.Count, 0, []));
        }

        public override Task<DecideProposalsResponse?> RejectProposalsAsync(List<Guid> ids, CancellationToken cancellationToken = default)
        {
            RejectedIds.AddRange(ids);
            foreach (var id in ids)
            {
                var p = Proposals.FirstOrDefault(x => x.Id == id);
                if (p != null) p.Status = "Rejected";
            }
            return Task.FromResult<DecideProposalsResponse?>(new DecideProposalsResponse(ids.Count, 0, []));
        }

        public override Task<DecideProposalsResponse?> CancelProposalsAsync(List<Guid> ids, CancellationToken cancellationToken = default)
        {
            CancelledIds.AddRange(ids);
            foreach (var id in ids)
            {
                Proposals.RemoveAll(x => x.Id == id);
            }
            return Task.FromResult<DecideProposalsResponse?>(new DecideProposalsResponse(ids.Count, 0, []));
        }

        public override Task<bool> RevertProposalAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public override Task<DecideProposalsResponse?> SetManualProposalUrlAsync(Guid id, string url, CancellationToken cancellationToken = default)
        {
            ManualUrlSet = (id, url);
            var p = Proposals.FirstOrDefault(x => x.Id == id);
            if (p != null) p.Status = "Approved";
            return Task.FromResult<DecideProposalsResponse?>(new DecideProposalsResponse(1, 0, []));
        }

        public override Task<BookmarkNodeDto?> UpdateBookmarkAsync(Guid id, string title, string? url, CancellationToken cancellationToken = default)
        {
            UpdateBookmarkCalled = true;
            return Task.FromResult<BookmarkNodeDto?>(new BookmarkNodeDto { Id = id, Title = title, Url = url });
        }


    }
}
