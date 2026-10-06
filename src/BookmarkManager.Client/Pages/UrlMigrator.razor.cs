using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class UrlMigrator : IDisposable
{
    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    private const string StatusPending = "Pending";
    private const string StatusApproved = "Approved";
    private const string ConfidenceHigh = "High";
    private const string ConfidenceUnresolved = "Unresolved";
    private const string DiscoveryPartialDetail = "partial: time budget reached";

    private List<DeadDomainCandidateDto> _deadDomains = [];
    private bool _loadingDeadDomains;
    private bool _deadDomainsExpanded;
    private const int DeadDomainsCollapsedCount = 10;
    private string _manualHost = string.Empty;
    private string _suggestedTargetHost = string.Empty;
    private string? _suggestedTargetPattern;
    private bool _discovering;
    private bool _discoveryPartial;
    private List<TargetHostSuggestionDto> _suggestions = [];
    private List<string> _rejectedHosts = [];
    private string? _discoveryMessage;
    private bool _starting;
    private bool _canceling;
    private UrlMigrationStatusDto? _status;
    private bool _polling;
    private CancellationTokenSource? _pollCts;
    private List<UrlMigrationProposalDto> _currentProposals = [];
    private List<UrlMigrationProposalDto> _allProposals = [];

    private SuwayomiStatusDto? _suwayomiStatus;
    private bool _suwayomiStatusLoading;
    private List<FolderTreeNodeDto> _folders = [];
    private Guid? _selectedFolderId;
    private SuwayomiImportPreviewDto? _suwayomiPreview;
    private bool _suwayomiPreviewLoading;
    private bool _suwayomiStarting;
    private bool _suwayomiCanceling;
    private SuwayomiImportStatusDto? _suwayomiImportStatus;
    private bool _suwayomiPolling;
    private CancellationTokenSource? _suwayomiPollCts;
    private List<UrlMigrationProposalDto> _suwayomiRunProposals = [];
    private bool IsRunning => _status?.IsRunning == true;

    private bool IsSuwayomiImportRunning => _suwayomiImportStatus?.IsRunning == true;

    private bool IsAnyRunning => IsRunning || IsSuwayomiImportRunning;

    private List<string> SuwayomiSearchOrder => _suwayomiStatus?.SearchOrder ?? [];

    private string SuwayomiConnState =>
        _suwayomiStatusLoading ? "checking" : _suwayomiStatus?.Reachable == true ? "ok" : "down";

    private string SuwayomiConnText =>
        _suwayomiStatusLoading ? "Checking…"
        : _suwayomiStatus?.Reachable == true
            ? $"Connected · {_suwayomiStatus.Version} · {_suwayomiStatus.SourceCount} sources"
        : "Suwayomi unreachable";

    private string SuwayomiStartText
    {
        get
        {
            var count = _suwayomiPreview?.ToImport ?? 0;
            return $"Import {count} bookmark{(count == 1 ? string.Empty : "s")}";
        }
    }

    private bool SuwayomiStartDisabled =>
        _suwayomiStarting
        || IsAnyRunning
        || _suwayomiStatus?.Reachable != true
        || (_suwayomiPreview?.ToImport ?? 0) <= 0;

    private List<FolderOption> FolderOptions => FlattenFolders(_folders);

    private sealed record FolderOption(Guid Id, string Path);

    // Test and Suggest sample the old host's bookmarks, so they stay disabled until it's picked.
    private string ManualRowHint =>
        string.IsNullOrWhiteSpace(_manualHost) ? "Pick the old host first. Test and Suggest check its bookmarks."
        : !string.IsNullOrWhiteSpace(_suggestedTargetHost) ? $"Search will be restricted to {_suggestedTargetHost} instead of the open web."
        : "Leave New host empty to search the open web, or press Suggest to find one.";

    protected override async Task OnInitializedAsync()
    {
        await LoadDeadDomainsAsync();
        await LoadRejectedHostsAsync();
        await RefreshStatusAsync();
        await LoadSuwayomiStatusAsync();
        await LoadFoldersAsync();
        await RefreshSuwayomiImportStatusAsync();

        if (IsRunning)
        {
            StartPolling();
        }
        else if (_status?.RunId != null || _suwayomiImportStatus?.RunId != null)
        {
            await LoadCurrentProposalsAsync();
        }

        if (IsSuwayomiImportRunning)
        {
            StartSuwayomiPolling();
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

    private async Task LoadSuwayomiStatusAsync()
    {
        _suwayomiStatusLoading = true;
        try
        {
            _suwayomiStatus = await BookmarkService.GetSuwayomiStatusAsync();
        }
        catch (Exception ex)
        {
            _suwayomiStatus = new SuwayomiStatusDto { Reachable = false };
            Snackbar.Add($"Failed to load Suwayomi status: {ex.Message}", Severity.Warning);
        }
        finally
        {
            _suwayomiStatusLoading = false;
        }
    }

    private async Task LoadFoldersAsync()
    {
        try
        {
            _folders = await BookmarkService.GetFolderTreeAsync();
            var options = FolderOptions;
            var manga = options.FirstOrDefault(f => string.Equals(f.Path.Split(" / ").LastOrDefault(), "Manga", StringComparison.OrdinalIgnoreCase));
            _selectedFolderId = manga?.Id ?? options.FirstOrDefault()?.Id;
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load folders: {ex.Message}", Severity.Error);
        }

        await LoadSuwayomiPreviewAsync();
    }

    private async Task OnFolderChangedAsync(Guid? folderId)
    {
        _selectedFolderId = folderId;
        await LoadSuwayomiPreviewAsync();
    }

    private async Task LoadSuwayomiPreviewAsync()
    {
        if (_selectedFolderId is not Guid folderId)
        {
            _suwayomiPreview = null;
            return;
        }

        _suwayomiPreviewLoading = true;
        try
        {
            _suwayomiPreview = await BookmarkService.GetSuwayomiImportPreviewAsync(folderId);
        }
        catch (Exception ex)
        {
            _suwayomiPreview = null;
            Snackbar.Add($"Failed to load import preview: {ex.Message}", Severity.Warning);
        }
        finally
        {
            _suwayomiPreviewLoading = false;
        }
    }

    private async Task RefreshSuwayomiImportStatusAsync()
    {
        try
        {
            _suwayomiImportStatus = await BookmarkService.GetSuwayomiImportStatusAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load Suwayomi import status: {ex.Message}", Severity.Error);
        }
    }

    private async Task RefreshSuwayomiAsync()
    {
        await LoadSuwayomiStatusAsync();
        await LoadSuwayomiPreviewAsync();
        StateHasChanged();
    }

    private async Task StartSuwayomiImportAsync()
    {
        if (_selectedFolderId is not Guid folderId || SuwayomiStartDisabled)
        {
            return;
        }

        _suwayomiStarting = true;
        try
        {
            var started = await BookmarkService.StartSuwayomiImportAsync(folderId);
            if (!started)
            {
                Snackbar.Add("Could not start the import — a run may already be in progress.", Severity.Error);
                return;
            }

            Snackbar.Add("Suwayomi import started.", Severity.Info);
            await RefreshSuwayomiImportStatusAsync();
            StartSuwayomiPolling();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Could not start the import: {ex.Message}", Severity.Error);
        }
        finally
        {
            _suwayomiStarting = false;
        }
    }

    private void StartSuwayomiPolling()
    {
        if (_suwayomiPolling)
        {
            return;
        }

        _suwayomiPolling = true;
        _suwayomiPollCts?.Cancel();
        _suwayomiPollCts = new CancellationTokenSource();
        _ = PollSuwayomiStatusLoopAsync(_suwayomiPollCts.Token);
    }

    private async Task PollSuwayomiStatusLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                await RefreshSuwayomiImportStatusAsync();
                await LoadCurrentProposalsAsync();
                StateHasChanged();

                if (!IsSuwayomiImportRunning)
                {
                    break;
                }
            }
        }
        catch (TaskCanceledException)
        {
            // Stopped intentionally.
        }
        finally
        {
            _suwayomiPolling = false;
        }

        if (!ct.IsCancellationRequested)
        {
            await LoadCurrentProposalsAsync();
            await LoadHistoryAsync();
            await LoadSuwayomiPreviewAsync();
            StateHasChanged();
        }
    }

    private async Task CancelSuwayomiImportAsync()
    {
        if (!IsSuwayomiImportRunning || _suwayomiCanceling)
        {
            return;
        }

        _suwayomiCanceling = true;
        try
        {
            if (!await BookmarkService.CancelSuwayomiImportAsync())
            {
                Snackbar.Add("The import is no longer running.", Severity.Warning);
            }
            else
            {
                Snackbar.Add("Cancel requested.", Severity.Info);
            }

            await RefreshSuwayomiImportStatusAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Could not cancel the import: {ex.Message}", Severity.Error);
        }
        finally
        {
            _suwayomiCanceling = false;
        }
    }

    private double SuwayomiProgressPercent()
        => _suwayomiImportStatus == null || _suwayomiImportStatus.TotalFound == 0
            ? 0
            : Math.Clamp(_suwayomiImportStatus.Processed * 100.0 / _suwayomiImportStatus.TotalFound, 0, 100);

    private async Task OpenSuwayomiDialogAsync(UrlMigrationProposalDto proposal)
    {
        var parameters = new DialogParameters<SuwayomiMatchDialog>
        {
            { x => x.ProposalId, proposal.Id },
            { x => x.BookmarkTitle, proposal.BookmarkTitle },
            { x => x.OldUrl, proposal.OldUrl },
            { x => x.InitialTitle, proposal.SeriesName ?? proposal.BookmarkTitle },
            { x => x.SourceOrder, SuwayomiSearchOrder }
        };

        var options = new DialogOptions { FullWidth = true, MaxWidth = MaxWidth.Medium };
        var dialog = await DialogService.ShowAsync<SuwayomiMatchDialog>("Find on Suwayomi", parameters, options);
        var dialogResult = await dialog.Result;
        if (dialogResult is null || dialogResult.Canceled)
        {
            return;
        }

        await LoadCurrentProposalsAsync();
        await LoadHistoryAsync();
        await LoadSuwayomiPreviewAsync();
        StateHasChanged();
    }

    private static string SuwayomiThumbnailUrl(int? mangaId)
        => mangaId is int id ? $"api/suwayomi/thumbnail/{id}" : string.Empty;

    private static bool ShowFromBookmark(UrlMigrationProposalDto proposal)
    {
        if (proposal.Confidence is "Medium" or "Low" or "Manual")
        {
            return true;
        }

        return !string.Equals(NormalizeForCompare(proposal.SeriesName), NormalizeForCompare(proposal.MatchedTitle), StringComparison.Ordinal);
    }

    private static bool IsChapterBeyondSource(UrlMigrationProposalDto proposal)
    {
        if (!TryParseChapter(proposal.ChapterNumber, out var chapter) || !TryParseChapter(proposal.SourceLatestChapter, out var latest))
        {
            return false;
        }

        return chapter > latest;
    }

    private static bool TryParseChapter(string? value, out double chapter)
    {
        chapter = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = System.Text.RegularExpressions.Regex.Match(value, @"\d+(?:\.\d+)?");
        return match.Success && double.TryParse(match.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out chapter);
    }

    private static string NormalizeForCompare(string? value)
        => System.Text.RegularExpressions.Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static List<FolderOption> FlattenFolders(List<FolderTreeNodeDto> nodes)
    {
        var result = new List<FolderOption>();
        void Walk(IEnumerable<FolderTreeNodeDto> items, string prefix)
        {
            foreach (var item in items)
            {
                var path = string.IsNullOrEmpty(prefix) ? item.Title : $"{prefix} / {item.Title}";
                result.Add(new FolderOption(item.Id, path));
                Walk(item.Children, path);
            }
        }

        Walk(nodes, string.Empty);
        return result;
    }

    private Task StartMigrationFromListAsync(string host) => StartMigrationAsync(host, force: false, suggestedHost: null);

    // Force = true: a manually-typed host is the user asserting the domain is dead, so skip the
    // "domain still appears alive" liveness guard that protects the auto-detected list. The learned
    // pattern (from "Suggest") is forwarded so the direct rewrite tries it first.
    private Task StartMigrationFromFieldAsync() =>
        StartMigrationAsync(_manualHost, force: true, suggestedHost: _suggestedTargetHost, pattern: _suggestedTargetPattern);

    private async Task SuggestTargetAsync()
    {
        var host = NormalizeHost(_manualHost);
        if (!IsValidHost(host))
        {
            Snackbar.Add("Enter the dead host first (e.g. flamecomics.xyz).", Severity.Warning);
            return;
        }

        _discovering = true;
        _discoveryMessage = null;
        _discoveryPartial = false;
        _suggestions = [];
        try
        {
            var result = await BookmarkService.DiscoverTargetHostAsync(host);
            if (result == null)
            {
                _discoveryMessage = "Target discovery returned no result.";
                return;
            }

            ApplyDiscoveryResult(result);
        }
        catch (Exception ex)
        {
            _discoveryMessage = $"Target discovery failed: {ex.Message}";
        }
        finally
        {
            _discovering = false;
        }
    }

    // "Test": skips search (0 credits) and probes only the New host field's host against the sample,
    // so the user can verify a specific candidate before starting a run.
    private async Task TestHostAsync()
    {
        var host = NormalizeHost(_manualHost);
        if (!IsValidHost(host))
        {
            Snackbar.Add("Enter the dead host first (e.g. flamecomics.xyz).", Severity.Warning);
            return;
        }

        var candidate = NormalizeHost(_suggestedTargetHost);
        if (!IsValidHost(candidate))
        {
            Snackbar.Add("Enter a valid host to test (e.g. comizy.io).", Severity.Warning);
            return;
        }

        _discovering = true;
        _discoveryMessage = null;
        _discoveryPartial = false;
        _suggestions = [];
        try
        {
            var result = await BookmarkService.DiscoverTargetHostAsync(host, null, [candidate]);
            if (result == null)
            {
                _discoveryMessage = "Target discovery returned no result.";
                return;
            }

            ApplyDiscoveryResult(result);
        }
        catch (Exception ex)
        {
            _discoveryMessage = $"Testing {candidate} failed: {ex.Message}";
        }
        finally
        {
            _discovering = false;
        }
    }

    private void ApplyDiscoveryResult(TargetHostDiscoveryResultDto result)
    {
        _suggestions = result.Suggestions;
        _discoveryPartial = string.Equals(result.Detail, DiscoveryPartialDetail, StringComparison.Ordinal);
        _discoveryMessage = _suggestions.Count == 0
            ? result.Detail ?? "No replacement host verified enough sampled series."
            : null;
    }

    private async Task LoadRejectedHostsAsync()
    {
        try
        {
            _rejectedHosts = await BookmarkService.GetRejectedTargetHostsAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load rejected hosts: {ex.Message}", Severity.Warning);
        }
    }

    private void AcceptSuggestion(TargetHostSuggestionDto suggestion)
    {
        _suggestedTargetHost = suggestion.Host;
        _suggestedTargetPattern = suggestion.BestPattern;
        Snackbar.Add($"Target set to {suggestion.Host}.", Severity.Success);
    }

    private async Task RejectSuggestionAsync(TargetHostSuggestionDto suggestion)
    {
        try
        {
            _rejectedHosts = await BookmarkService.RejectTargetHostAsync(suggestion.Host);
            _suggestions.RemoveAll(s => string.Equals(s.Host, suggestion.Host, StringComparison.OrdinalIgnoreCase));
            Snackbar.Add($"{suggestion.Host} will never be suggested again.", Severity.Info);
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to reject {suggestion.Host}: {ex.Message}", Severity.Error);
        }
    }

    private async Task UnrejectHostAsync(string host)
    {
        try
        {
            _rejectedHosts = await BookmarkService.UnrejectTargetHostAsync(host);
            Snackbar.Add($"{host} can be suggested again.", Severity.Info);
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to un-reject {host}: {ex.Message}", Severity.Error);
        }
    }

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

    private async Task StartMigrationAsync(string? host, bool force, string? suggestedHost, string? pattern = null)
    {
        host = NormalizeHost(host);
        if (!IsValidHost(host))
        {
            Snackbar.Add("Enter a valid hostname (e.g. flamecomics.xyz or https://flamecomics.xyz).", Severity.Warning);
            return;
        }

        suggestedHost = NormalizeHost(suggestedHost);
        if (!string.IsNullOrEmpty(suggestedHost) && !IsValidHost(suggestedHost))
        {
            Snackbar.Add("Suggested target host must be a valid hostname (e.g. weebcentral.com or https://weebcentral.com).", Severity.Warning);
            return;
        }

        _starting = true;
        try
        {
            var started = await BookmarkService.StartUrlMigrationAsync(host, force,
                string.IsNullOrEmpty(suggestedHost) ? null : suggestedHost,
                string.IsNullOrEmpty(pattern) ? null : pattern);
            if (!started)
            {
                Snackbar.Add("Could not start migration - a run may already be in progress.", Severity.Error);
                return;
            }

            Snackbar.Add($"Migration started for {host}.", Severity.Info);
            _manualHost = string.Empty;
            _suggestedTargetHost = string.Empty;
            _suggestedTargetPattern = null;
            _suggestions = [];
            _discoveryMessage = null;
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

    private async Task CancelMigrationAsync()
    {
        if (!IsRunning || _canceling)
        {
            return;
        }

        _canceling = true;
        try
        {
            if (!await BookmarkService.CancelUrlMigrationAsync())
            {
                Snackbar.Add("The migration is no longer running.", Severity.Warning);
            }
            else
            {
                Snackbar.Add("Cancel requested.", Severity.Info);
            }

            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Could not cancel migration: {ex.Message}", Severity.Error);
        }
        finally
        {
            _canceling = false;
        }
    }

    private async Task ResetEngineAsync()
    {
        try
        {
            _pollCts?.Cancel();
            _polling = false;
            await BookmarkService.ResetUrlMigrationAsync();
            await RefreshStatusAsync();
            await LoadCurrentProposalsAsync();
            await LoadHistoryAsync();
            Snackbar.Add("Migration engine was reset.", Severity.Info);
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to reset engine: {ex.Message}", Severity.Error);
        }
    }

    private async Task CopyToClipboardAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            await JsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", text);
            Snackbar.Add("Copied URL to clipboard", Severity.Success);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to copy URL: {ex.Message}", Severity.Warning);
        }
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
        var combined = new List<UrlMigrationProposalDto>();
        _suwayomiRunProposals = [];

        try
        {
            if (_status?.RunId != null)
            {
                combined.AddRange(await BookmarkService.GetUrlMigrationProposalsAsync(_status.RunId, null));
            }

            if (_suwayomiImportStatus?.RunId != null)
            {
                _suwayomiRunProposals = await BookmarkService.GetUrlMigrationProposalsAsync(_suwayomiImportStatus.RunId, null);
                combined.AddRange(_suwayomiRunProposals);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to load proposals: {ex.Message}", Severity.Error);
        }

        _currentProposals = combined
            .GroupBy(p => p.Id)
            .Select(g => g.First())
            .ToList();
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
            .Where(p => p.Status == StatusPending
                && !p.IsSuwayomi
                && !string.IsNullOrEmpty(p.ProposedHost)
                && p.Confidence != ConfidenceUnresolved)
            .GroupBy(p => p.ProposedHost!);

    /// <summary>Pending Suwayomi matches, grouped by source and ordered by the configured search order.</summary>
    private IEnumerable<IGrouping<string, UrlMigrationProposalDto>> SuwayomiPendingGroups =>
        _currentProposals
            .Where(p => p.Status == StatusPending && p.SuwayomiMangaId != null)
            .GroupBy(p => p.SourceName ?? string.Empty)
            .OrderBy(g => SourceOrderIndex(g.Key));

    private int SourceOrderIndex(string sourceName)
    {
        var order = SuwayomiSearchOrder;
        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], sourceName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private List<UrlMigrationProposalDto> UnresolvedProposals =>
        _currentProposals
            .Where(p => p.Status == StatusPending
                && p.SuwayomiMangaId == null
                && (string.IsNullOrEmpty(p.ProposedHost) || p.Confidence == ConfidenceUnresolved))
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

        var suwayomiApproved = verb == "approved"
            && idList.Any(id => FindProposal(id)?.SuwayomiMangaId != null);

        if (result.Failed > 0)
        {
            var suwayomiFailure = suwayomiApproved
                && result.Errors?.Any(e => e.Contains("Suwayomi", StringComparison.OrdinalIgnoreCase)) == true;
            if (suwayomiFailure)
            {
                Snackbar.Add("Suwayomi didn't respond — nothing was changed.", Severity.Error);
            }
            else
            {
                var failedTitles = idList
                    .Select(FindProposal)
                    .Where(p => p != null)
                    .Select(p => p!.BookmarkTitle)
                    .ToList();
                var detail = failedTitles.Count > 0 ? string.Join(", ", failedTitles) : string.Join("; ", result.Errors);
                Snackbar.Add($"{result.Succeeded} {verb}, {result.Failed} failed: {detail}", Severity.Warning);
            }
        }
        else if (suwayomiApproved && result.Succeeded > 0)
        {
            var note = result.Messages?.FirstOrDefault() ?? "nothing marked read";
            Snackbar.Add($"Added to Suwayomi · {note}", Severity.Success);
        }
        else
        {
            Snackbar.Add($"{result.Succeeded} proposal(s) {verb}.", Severity.Success);
        }

        await LoadCurrentProposalsAsync();
        await LoadHistoryAsync();
        StateHasChanged();
    }

    private UrlMigrationProposalDto? FindProposal(Guid id)
        => _currentProposals.FirstOrDefault(p => p.Id == id) ?? _allProposals.FirstOrDefault(p => p.Id == id);

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
            var isSuwayomi = FindProposal(id)?.SuwayomiMangaId != null;
            var ok = await BookmarkService.RevertProposalAsync(id);
            var message = ok
                ? isSuwayomi
                    ? "Reverted — bookmark restored and series removed from your Suwayomi library."
                    : "Proposal reverted."
                : "Could not revert proposal.";
            Snackbar.Add(message, ok ? Severity.Success : Severity.Error);
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
            { x => x.BookmarkTitle, proposal.BookmarkTitle },
            { x => x.InitialUrl, string.Empty },
            { x => x.Title, "Enter URL manually" }
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

    private async Task OpenEditProposalUrlDialogAsync(UrlMigrationProposalDto proposal)
    {
        var parameters = new DialogParameters<ManualUrlDialog>
        {
            { x => x.BookmarkTitle, proposal.BookmarkTitle },
            { x => x.InitialUrl, proposal.ProposedUrl ?? string.Empty },
            { x => x.Title, "Edit Proposed URL" }
        };

        var dialog = await DialogService.ShowAsync<ManualUrlDialog>("Edit Proposed URL", parameters);
        var dialogResult = await dialog.Result;
        if (dialogResult is null || dialogResult.Canceled)
            return;

        if (dialogResult.Data is not string url || string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            var updated = await BookmarkService.UpdateProposalUrlAsync(proposal.Id, url);
            if (updated != null)
            {
                proposal.ProposedUrl = updated.ProposedUrl;
                proposal.ProposedHost = updated.ProposedHost;
                proposal.Confidence = updated.Confidence;
                proposal.Detail = updated.Detail;
                Snackbar.Add("Proposed URL updated.", Severity.Success);
                await LoadCurrentProposalsAsync();
                StateHasChanged();
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to update proposed URL: {ex.Message}", Severity.Error);
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

    private static string NormalizeHost(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        input = input.Trim();
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
            {
                return uri.Host;
            }
        }
        else if (input.Contains('/'))
        {
            var firstPart = input.Split('/', StringSplitOptions.RemoveEmptyEntries)[0];
            return firstPart.Trim();
        }

        return input;
    }

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
        _suwayomiPollCts?.Cancel();
        _suwayomiPollCts?.Dispose();
    }
}
