using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class UrlMigrator : IDisposable
{
    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;

    private const string StatusPending = "Pending";
    private const string StatusApproved = "Approved";
    private const string ConfidenceHigh = "High";
    private const string ConfidenceUnresolved = "Unresolved";

    private List<DeadDomainCandidateDto> _deadDomains = [];
    private bool _loadingDeadDomains;
    private bool _deadDomainsExpanded;
    private const int DeadDomainsCollapsedCount = 10;
    private string _manualHost = string.Empty;
    private string _suggestedTargetHost = string.Empty;
    private bool _starting;
    private UrlMigrationStatusDto? _status;
    private bool _polling;
    private CancellationTokenSource? _pollCts;
    private List<UrlMigrationProposalDto> _currentProposals = [];
    private List<UrlMigrationProposalDto> _allProposals = [];

    private bool IsRunning => _status?.IsRunning == true;

    protected override async Task OnInitializedAsync()
    {
        await LoadDeadDomainsAsync();
        await RefreshStatusAsync();

        if (IsRunning)
        {
            StartPolling();
        }
        else if (_status?.RunId != null)
        {
            await LoadCurrentProposalsAsync();
        }

        await LoadHistoryAsync();
    }

    private async Task LoadDeadDomainsAsync()
    {
        _loadingDeadDomains = true;
        try
        {
            _deadDomains = await BookmarkService.GetDeadDomainCandidatesAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load dead domains: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loadingDeadDomains = false;
        }
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            _status = await BookmarkService.GetUrlMigrationStatusAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load migration status: {ex.Message}", Severity.Error);
        }
    }

    private Task StartMigrationFromListAsync(string host) => StartMigrationAsync(host, force: false, suggestedHost: null);

    // Force = true: a manually-typed host is the user asserting the domain is dead, so skip the
    // "domain still appears alive" liveness guard that protects the auto-detected list.
    private Task StartMigrationFromFieldAsync() => StartMigrationAsync(_manualHost, force: true, suggestedHost: _suggestedTargetHost);

    private async Task<IEnumerable<string>> SearchHostsAsync(string? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var result = await BookmarkService.SearchBookmarksAsync(
            new SearchRequest { Query = value, PageSize = 50 },
            cancellationToken);

        return result.Items
            .Where(b => !string.IsNullOrWhiteSpace(b.Url))
            .Select(b => Uri.TryCreate(b.Url, UriKind.Absolute, out var uri) ? uri.Host : null)
            .Where(host => host is not null && host.Contains(value, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .Take(8)!;
    }

    private async Task StartMigrationAsync(string? host, bool force, string? suggestedHost)
    {
        host = host?.Trim() ?? string.Empty;
        if (!IsValidHost(host))
        {
            Snackbar.Add("Enter a valid hostname (no scheme, path, or spaces).", Severity.Warning);
            return;
        }

        suggestedHost = suggestedHost?.Trim();
        if (!string.IsNullOrEmpty(suggestedHost) && !IsValidHost(suggestedHost))
        {
            Snackbar.Add("Suggested target host must be a valid hostname (no scheme, path, or spaces).", Severity.Warning);
            return;
        }

        _starting = true;
        try
        {
            var started = await BookmarkService.StartUrlMigrationAsync(host, force, string.IsNullOrEmpty(suggestedHost) ? null : suggestedHost);
            if (!started)
            {
                Snackbar.Add("Could not start migration - a run may already be in progress.", Severity.Error);
                return;
            }

            Snackbar.Add($"Migration started for {host}.", Severity.Info);
            _manualHost = string.Empty;
            _suggestedTargetHost = string.Empty;
            await RefreshStatusAsync();
            StartPolling();
        }
        finally
        {
            _starting = false;
        }
    }

    private void StartPolling()
    {
        if (_polling)
            return;

        _polling = true;
        _pollCts?.Cancel();
        _pollCts = new CancellationTokenSource();
        _ = PollStatusLoopAsync(_pollCts.Token);
    }

    private async Task PollStatusLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                await RefreshStatusAsync();
                StateHasChanged();

                if (!IsRunning)
                    break;
            }
        }
        catch (TaskCanceledException)
        {
            // Stopped intentionally (component disposed or a new run started).
        }
        finally
        {
            _polling = false;
        }

        if (!ct.IsCancellationRequested)
        {
            await LoadCurrentProposalsAsync();
            await LoadHistoryAsync();
            StateHasChanged();
        }
    }

    private async Task LoadCurrentProposalsAsync()
    {
        if (_status?.RunId == null)
        {
            _currentProposals = [];
            return;
        }

        try
        {
            _currentProposals = await BookmarkService.GetUrlMigrationProposalsAsync(_status.RunId, null);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load proposals: {ex.Message}", Severity.Error);
        }
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            _allProposals = await BookmarkService.GetUrlMigrationProposalsAsync(null, null);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load history: {ex.Message}", Severity.Error);
        }
    }

    private IEnumerable<IGrouping<string, UrlMigrationProposalDto>> PendingGroups =>
        _currentProposals
            .Where(p => p.Status == StatusPending && !string.IsNullOrEmpty(p.ProposedHost) && p.Confidence != ConfidenceUnresolved)
            .GroupBy(p => p.ProposedHost!);

    private List<UrlMigrationProposalDto> UnresolvedProposals =>
        _currentProposals
            .Where(p => p.Status == StatusPending && (string.IsNullOrEmpty(p.ProposedHost) || p.Confidence == ConfidenceUnresolved))
            .ToList();

    private List<UrlMigrationProposalDto> HighConfidencePending =>
        _currentProposals.Where(p => p.Status == StatusPending && p.Confidence == ConfidenceHigh).ToList();

    /// <summary>Decided proposals across all runs (used by the History tab, since RunId is not exposed on the DTO).</summary>
    private List<UrlMigrationProposalDto> HistoryProposals =>
        _allProposals.Where(p => p.Status != StatusPending).OrderByDescending(p => p.CreatedAt).ToList();

    /// <summary>
    /// Pending proposals left over from an earlier run (e.g. one that was interrupted, or whose
    /// tab was closed before review). These bookmarks are excluded from future runs for the same
    /// host until decided, but RunId isn't exposed on the DTO, so "belongs to an earlier run" is
    /// inferred as: Pending, but not present in the current run's proposal list.
    /// </summary>
    private List<UrlMigrationProposalDto> OrphanedPendingProposals =>
        _allProposals
            .Where(p => p.Status == StatusPending && _currentProposals.All(c => c.Id != p.Id))
            .OrderByDescending(p => p.CreatedAt)
            .ToList();

    private async Task ApproveAsync(IEnumerable<Guid> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
            return;

        try
        {
            var result = await BookmarkService.ApproveProposalsAsync(idList);
            await HandleDecisionResultAsync(result, idList, "approved");
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to approve proposal(s): {ex.Message}", Severity.Error);
        }
    }

    private async Task RejectAsync(IEnumerable<Guid> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
            return;

        try
        {
            var result = await BookmarkService.RejectProposalsAsync(idList);
            await HandleDecisionResultAsync(result, idList, "rejected");
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to reject proposal(s): {ex.Message}", Severity.Error);
        }
    }

    private async Task HandleDecisionResultAsync(DecideProposalsResponse? result, List<Guid> idList, string verb)
    {
        if (result == null)
        {
            Snackbar.Add($"No response received for the {verb} request.", Severity.Error);
            return;
        }

        if (result.Failed > 0)
        {
            var failedTitles = idList
                .Select(id => _currentProposals.FirstOrDefault(p => p.Id == id))
                .Where(p => p != null)
                .Select(p => p!.BookmarkTitle)
                .ToList();
            var detail = failedTitles.Count > 0 ? string.Join(", ", failedTitles) : string.Join("; ", result.Errors);
            Snackbar.Add($"{result.Succeeded} {verb}, {result.Failed} failed: {detail}", Severity.Warning);
        }
        else
        {
            Snackbar.Add($"{result.Succeeded} proposal(s) {verb}.", Severity.Success);
        }

        await LoadCurrentProposalsAsync();
        await LoadHistoryAsync();
        StateHasChanged();
    }

    // Cancel differs from Reject: Reject means "I saw this URL and don't want it" and blocks it
    // from being re-suggested; Cancel just voids a stale proposal so the bookmark is free for a
    // completely fresh run next time.
    private async Task CancelAsync(IEnumerable<Guid> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0)
            return;

        try
        {
            var result = await BookmarkService.CancelProposalsAsync(idList);
            await HandleDecisionResultAsync(result, idList, "cancelled");
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to cancel proposal(s): {ex.Message}", Severity.Error);
        }
    }

    private Task CancelAllOrphanedAsync() => CancelAsync(OrphanedPendingProposals.Select(p => p.Id));

    private Task ApproveAllHighAsync() => ApproveAsync(HighConfidencePending.Select(p => p.Id));

    private Task RejectRemainingAsync() =>
        RejectAsync(_currentProposals.Where(p => p.Status == StatusPending).Select(p => p.Id));

    private async Task RevertAsync(Guid id)
    {
        try
        {
            var ok = await BookmarkService.RevertProposalAsync(id);
            Snackbar.Add(ok ? "Proposal reverted." : "Could not revert proposal.", ok ? Severity.Success : Severity.Error);
            if (ok)
            {
                await LoadHistoryAsync();
                await LoadCurrentProposalsAsync();
                StateHasChanged();
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to revert proposal: {ex.Message}", Severity.Error);
        }
    }

    private async Task OpenManualUrlDialogAsync(UrlMigrationProposalDto proposal)
    {
        var parameters = new DialogParameters<ManualUrlDialog>
        {
            { x => x.BookmarkTitle, proposal.BookmarkTitle }
        };

        var dialog = await DialogService.ShowAsync<ManualUrlDialog>("Enter URL manually", parameters);
        var dialogResult = await dialog.Result;
        if (dialogResult is null || dialogResult.Canceled)
            return;

        if (dialogResult.Data is not string url || string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            var result = await BookmarkService.SetManualProposalUrlAsync(proposal.Id, url);
            await HandleDecisionResultAsync(result, [proposal.Id], "approved");
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to update bookmark: {ex.Message}", Severity.Error);
        }
    }

    private async Task EditBookmarkAsync(UrlMigrationProposalDto proposal)
    {
        BookmarkNodeDto? node;
        try
        {
            node = await BookmarkService.GetBookmarkAsync(proposal.BookmarkId);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load bookmark: {ex.Message}", Severity.Error);
            return;
        }

        if (node == null)
        {
            Snackbar.Add("Bookmark no longer exists.", Severity.Error);
            return;
        }

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium };
        var parameters = new DialogParameters { ["Node"] = node };
        var dialog = await DialogService.ShowAsync<BookmarkEditDialog>("Edit Bookmark", parameters, options);
        var dialogResult = await dialog.Result;
        if (dialogResult is null || dialogResult.Canceled || dialogResult.Data is not BookmarkEditDialog.BookmarkEditResult data)
            return;

        try
        {
            // Title/Url only - tags/metadata aren't surfaced here, so leave them untouched
            // rather than round-tripping a possibly-stale metadata snapshot.
            await BookmarkService.UpdateBookmarkAsync(node.Id, data.Title, data.Url);
            Snackbar.Add("Bookmark updated.", Severity.Success);
            await LoadHistoryAsync();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to update bookmark: {ex.Message}", Severity.Error);
        }
    }

    private static string ConfidenceBadgeClass(string confidence) => confidence switch
    {
        "High" => "status-badge--success",
        "Medium" => "status-badge--warning",
        "Low" => "status-badge--danger",
        _ => "status-badge--watching",
    };

    private static string HistoryStatusBadgeClass(string status) => status switch
    {
        StatusApproved => "status-badge--success",
        "Rejected" => "status-badge--danger",
        "Cancelled" => "status-badge--warning",
        "Reverted" => "status-badge--watching",
        _ => "status-badge--active",
    };

    private static bool IsValidHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (host.Any(char.IsWhiteSpace) || host.Contains('/') || host.Contains('?') || host.Contains('\\'))
            return false;

        return Uri.CheckHostName(host) != UriHostNameType.Unknown;
    }

    public void Dispose()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
    }
}
