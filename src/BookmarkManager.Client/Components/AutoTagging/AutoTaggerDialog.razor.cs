using BookmarkManager.Client.Services;
using BookmarkManager.Client.Services.AutoTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;

namespace BookmarkManager.Client.Components;

public partial class AutoTaggerDialog
{
    [CascadingParameter] private IMudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public Guid? CurrentFolderId { get; set; }
    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;

    // Reruns panel state
    private bool _isEditingTags;
    private List<AiAutoTagBookmarkStatusDto> _runResults = [];
    private HashSet<Guid> _selectedResultIds = [];
    private string _rerunStatusFilter = "All";
    private bool _rerunsLoading;
    private Dictionary<Guid, string> _editTagsBuffer = new();
    private List<ProviderTimingDto> _providerTimings = [];

    private IReadOnlyList<string> RerunStatusOptions =>
        new[] { "All" }
            .Concat(_runResults.Select(r => r.Status).Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            .ToList();

    private List<AiAutoTagBookmarkStatusDto> FilteredRunResults =>
        _rerunStatusFilter == "All"
            ? _runResults
            : _runResults.Where(r => r.Status == _rerunStatusFilter).ToList();

    private static Color GetStatusColor(string status) => status switch
    {
        "DeterministicClassified" or "AiIdentified" or "ManualEdit" => Color.Success,
        "LowConfidence" or "NoSourceTags" or "AiPendingRetry" => Color.Warning,
        "ProviderFailed" or "RateLimited" or "AiInvalidResponse" => Color.Error,
        _ => Color.Default
    };

    private void GoToReruns()
    {
        _rerunStatusFilter = "All";
        _currentState = TaggerState.Reruns;
    }

    private void ResetRerunsState()
    {
        _runResults.Clear();
        _selectedResultIds.Clear();
        _editTagsBuffer.Clear();
        _providerTimings.Clear();
        _isEditingTags = false;
        _rerunStatusFilter = "All";
    }

    private void MergeRunResults(IEnumerable<AiAutoTagBookmarkStatusDto> statuses)
    {
        foreach (var status in statuses)
        {
            var existingIndex = _runResults.FindIndex(r => r.BookmarkId == status.BookmarkId);
            if (existingIndex >= 0)
                _runResults[existingIndex] = status;
            else
                _runResults.Add(status);
        }
    }

    private void MergeProviderTimings(IEnumerable<ProviderTimingDto>? timings)
    {
        if (timings is null)
            return;

        foreach (var timing in timings)
        {
            var existing = _providerTimings.FirstOrDefault(t =>
                t.Provider == timing.Provider && t.Operation == timing.Operation);
            if (existing is null)
            {
                _providerTimings.Add(new ProviderTimingDto
                {
                    Provider = timing.Provider,
                    Operation = timing.Operation,
                    NetworkCalls = timing.NetworkCalls,
                    CacheHits = timing.CacheHits,
                    LimiterMs = timing.LimiterMs,
                    HttpMs = timing.HttpMs
                });
            }
            else
            {
                existing.NetworkCalls += timing.NetworkCalls;
                existing.CacheHits += timing.CacheHits;
                existing.LimiterMs += timing.LimiterMs;
                existing.HttpMs += timing.HttpMs;
            }
        }
    }

    private async Task RunAiSeriesAutoTaggingAsync()
    {
        int batchSize = Math.Clamp(_preferredBatchSize, 1, AutoTaggerUiTiming.MaxUiBatchSize);
        bool stoppedEarly = false;

        if (_preferredBatchSize > AutoTaggerUiTiming.MaxUiBatchSize)
        {
            AddLog($"Using batch size {batchSize} for live progress (settings allow up to {_preferredBatchSize}).", LogType.Info);
        }

        foreach (var folderId in _selectedFolderIds)
        {
            if (_cts!.IsCancellationRequested)
                break;

            var folder = _selectableFolders.First(f => f.Id == folderId);
            var excludedBookmarkIds = new HashSet<Guid>();
            var folderProcessed = 0;
            var totalTagged = 0;
            var totalAlreadyTagged = 0;
            var totalLowConfidence = 0;
            var totalNoSourceTags = 0;
            var totalFailedChunks = 0;
            var folderTotal = _untaggedCounts.GetValueOrDefault(folderId, 0);

            _statusText = $"AI tagging '{folder.Title}'...";
            AddLog($"Running OpenRouter series matching for '{folder.Title}'...", LogType.Info);
            AddLog($"Processing in batches of {batchSize}; progress updates after each bookmark in a batch.", LogType.Info);
            StateHasChanged();

            var batchNumber = 1;
            while (!_cts.IsCancellationRequested)
            {
                _currentBookmarkTitle = null;
                AddLog($"→ Batch {batchNumber}: requesting up to {batchSize} bookmark(s) from server...", LogType.Info);
                StateHasChanged();
                await ScrollTerminalToBottomAsync();

                var summary = await RequestAiBatchWithHeartbeatAsync(folderId, batchSize, excludedBookmarkIds, batchNumber);

                if (summary.StopForRateLimit)
                {
                    _rateLimitStopped = true;
                    _remainingCount = _totalCount - _processedCount;
                    AddLog("OpenRouter rate limit reached. Pausing tagging to avoid further errors.", LogType.Error);
                    AddLog($"[INFO] {_remainingCount} bookmark(s) remain untagged.", LogType.Info);
                    AddLog("[INFO] Please wait a moment (e.g. 60 seconds) and run the Auto Tagger again to resume.", LogType.Info);
                    stoppedEarly = true;
                    break;
                }

                await ApplyBatchProgressAsync(summary, batchNumber, excludedBookmarkIds);

                var processedThisBatch = summary.ProcessedBookmarkIds.Count;
                folderProcessed += processedThisBatch;
                totalTagged += summary.Tagged;
                totalAlreadyTagged += summary.SkippedAlreadyTagged;
                totalLowConfidence += summary.SkippedLowConfidence;
                totalNoSourceTags += summary.SkippedNoSourceTags;
                totalFailedChunks += summary.FailedChunks;

                AddLog(
                    $"✓ Batch {batchNumber} returned: processed {processedThisBatch}, tagged {summary.Tagged}, low confidence {summary.SkippedLowConfidence}, no source tags {summary.SkippedNoSourceTags}, failed chunks {summary.FailedChunks}.",
                    summary.Tagged > 0 ? LogType.Success : LogType.Info);

                _statusText = $"AI tagging '{folder.Title}' ({folderProcessed} / {folderTotal})...";
                RefreshEta();
                StateHasChanged();
                await ScrollTerminalToBottomAsync();

                if (_cts.IsCancellationRequested)
                {
                    AddLog("Stop requested; completed bookmarks in this batch were saved on the server.", LogType.Info);
                    stoppedEarly = true;
                    break;
                }

                if (processedThisBatch == 0)
                {
                    _rateLimitStopped = false;
                    _remainingCount = _totalCount - _processedCount;
                    AddLog("No bookmarks were processed in this batch. Stopping so these bookmarks can be retried later instead of being skipped.", summary.FailedChunks > 0 ? LogType.Error : LogType.Info);
                    stoppedEarly = true;
                    break;
                }

                if (!summary.HasMore)
                    break;

                batchNumber++;
            }

            if (stoppedEarly)
                break;

            AddLog(
                $"✓ '{folder.Title}' complete: tagged {totalTagged}, already tagged {totalAlreadyTagged}, low confidence {totalLowConfidence}, no source tags {totalNoSourceTags}, failed chunks {totalFailedChunks}.",
                totalTagged > 0 ? LogType.Success : LogType.Info);
            StateHasChanged();
            await ScrollTerminalToBottomAsync();
        }

        _currentBookmarkTitle = null;
        if (!stoppedEarly)
            AddLog("AI auto-tagging complete. Tags were saved directly to the database.", LogType.Success);
        else
            AddLog("AI auto-tagging paused. Completed bookmarks were saved; rerun to resume.", LogType.Error);

        _taggingFinished = true;
        if (_runResults.Count > 0)
            AddLog("Click 'Results & Reruns' to review statuses, edit tags, or rerun skipped bookmarks.", LogType.Info);
    }

    private async Task<AiAutoTagSummaryDto> RequestAiBatchWithHeartbeatAsync(
        Guid folderId,
        int batchSize,
        HashSet<Guid> excludedBookmarkIds,
        int batchNumber)
    {
        var batchStarted = DateTimeOffset.UtcNow;
        var requestTask = BookmarkService.AiAutoTagFolderBatchAsync(folderId, new AiAutoTagBatchRequestDto
        {
            ForceRefresh = false,
            MaxCandidates = batchSize,
            ExcludedBookmarkIds = excludedBookmarkIds.ToList()
        }, CancellationToken.None);

        return await AutoTaggerAiBatchHeartbeat.WaitForCompletionAsync(
            requestTask,
            batchStarted,
            AutoTaggerUiTiming.BatchHeartbeatInterval,
            async elapsed =>
            {
                _statusText = _cts!.IsCancellationRequested
                    ? $"Finishing batch {batchNumber} ({elapsed:mm\\:ss} elapsed) before stop..."
                    : $"AI batch {batchNumber} in progress ({elapsed:mm\\:ss} elapsed)...";
                RefreshEta();
                await InvokeAsync(StateHasChanged);
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ApplyBatchProgressAsync(
        AiAutoTagSummaryDto summary,
        int batchNumber,
        HashSet<Guid> excludedBookmarkIds)
    {
        var statusById = summary.BookmarkStatuses
            .GroupBy(status => status.BookmarkId)
            .ToDictionary(group => group.Key, group => group.Last());

        foreach (var bookmarkId in summary.ProcessedBookmarkIds)
        {
            if (statusById.TryGetValue(bookmarkId, out var status))
            {
                _currentBookmarkTitle = status.Title;
                var logType = status.Status is "DeterministicClassified" or "AiIdentified"
                    ? LogType.Success
                    : status.Status is "RateLimited" or "ProviderFailed" or "AiInvalidResponse"
                        ? LogType.Error
                        : LogType.Info;
                AddLog($"[{batchNumber}] {status.Title}: {status.Status} — {status.Reason}", logType);
            }
            else
            {
                _currentBookmarkTitle = $"Bookmark {bookmarkId}";
                AddLog($"[{batchNumber}] Processed bookmark {bookmarkId}", LogType.Info);
            }

            _processedCount++;
            RefreshEta();
            await InvokeAsync(StateHasChanged);
            await ScrollTerminalToBottomAsync();
        }

        foreach (var id in summary.ProcessedBookmarkIds)
            excludedBookmarkIds.Add(id);

        foreach (var message in summary.Messages.Where(AiAutoTagSummaryMessageFilter.ShouldDisplay))
        {
            var logType = message.Contains('✓', StringComparison.Ordinal) ? LogType.Success
                : message.Contains('✗', StringComparison.Ordinal) ? LogType.Error
                : LogType.Info;
            AddLog(message, logType);
        }

        MergeRunResults(summary.BookmarkStatuses);
        MergeProviderTimings(summary.ProviderTimings);
    }

    private void RefreshEta()
        => _etaText = AutoTagProgressEstimator.EstimateRemaining(_taggingStartedAt, _processedCount, _totalCount);

    private void ToggleResultSelection(Guid bookmarkId, bool isChecked)
    {
        if (isChecked)
            _selectedResultIds.Add(bookmarkId);
        else
            _selectedResultIds.Remove(bookmarkId);
    }

    private void ToggleSelectAllFiltered(bool selectAll)
    {
        if (selectAll)
        {
            foreach (var item in FilteredRunResults)
                _selectedResultIds.Add(item.BookmarkId);
        }
        else
        {
            foreach (var item in FilteredRunResults)
                _selectedResultIds.Remove(item.BookmarkId);
        }
    }

    private async Task RerunSelectedAsync()
    {
        if (_selectedResultIds.Count == 0)
            return;

        _rerunsLoading = true;
        StateHasChanged();

        try
        {
            var pendingIds = _selectedResultIds.ToList();
            var batchSize = Math.Clamp(_preferredBatchSize, 1, AutoTaggerUiTiming.MaxUiBatchSize);
            var taggedCount = 0;
            var rateLimited = false;
            AddLog($"Rerunning {pendingIds.Count} bookmark(s) in batches of {batchSize}...", LogType.Info);

            for (var offset = 0; offset < pendingIds.Count; offset += batchSize)
            {
                var batchIds = pendingIds.Skip(offset).Take(batchSize).ToList();
                var summary = await BookmarkService.RerunTagsAsync(
                    new RerunBookmarksRequestDto { BookmarkIds = batchIds });

                MergeRunResults(summary.BookmarkStatuses);
                MergeProviderTimings(summary.ProviderTimings);

                foreach (var id in batchIds)
                    _selectedResultIds.Remove(id);

                foreach (var msg in summary.Messages.Where(AiAutoTagSummaryMessageFilter.ShouldDisplay))
                {
                    var logType = msg.Contains('✓') ? LogType.Success
                        : msg.Contains('✗') ? LogType.Error
                        : LogType.Info;
                    AddLog(msg, logType);
                }

                taggedCount += summary.BookmarkStatuses.Count(s => s.Status is "DeterministicClassified" or "AiIdentified");
                StateHasChanged();

                if (summary.StopForRateLimit)
                {
                    rateLimited = true;
                    AddLog("Rate limit hit during rerun; remaining bookmarks stay selected. Wait a bit and rerun again.", LogType.Error);
                    break;
                }
            }

            if (rateLimited)
                Snackbar.Add($"Rerun paused by rate limit after tagging {taggedCount}. Remaining bookmarks stay selected.", Severity.Warning);
            else
                Snackbar.Add($"Rerun complete: {taggedCount} tagged.", taggedCount > 0 ? Severity.Success : Severity.Info);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Rerun failed: {ex.Message}", Severity.Error);
            AddLog($"Rerun error: {ex.Message}", LogType.Error);
        }
        finally
        {
            _rerunsLoading = false;
            StateHasChanged();
        }
    }

    private void StartEditTags()
    {
        _isEditingTags = true;
        _editTagsBuffer.Clear();
        foreach (var item in _runResults.Where(r => _selectedResultIds.Contains(r.BookmarkId)))
            _editTagsBuffer[item.BookmarkId] = item.Tags ?? string.Empty;
    }

    private void CancelEditTags()
    {
        _isEditingTags = false;
        _editTagsBuffer.Clear();
    }

    private async Task SaveEditedTagsAsync()
    {
        if (_editTagsBuffer.Count == 0)
            return;

        _rerunsLoading = true;
        StateHasChanged();

        try
        {
            var tagsDict = _editTagsBuffer.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());

            var success = await BookmarkService.BulkSaveTagsAsync(new BulkSaveTagsRequest { Tags = tagsDict });

            if (success)
            {
                foreach (var kvp in tagsDict)
                {
                    var index = _runResults.FindIndex(r => r.BookmarkId == kvp.Key);
                    if (index >= 0)
                    {
                        _runResults[index].Tags = string.Join(",", kvp.Value);
                        _runResults[index].Status = "ManualEdit";
                    }
                }

                Snackbar.Add($"Saved tags for {_editTagsBuffer.Count} bookmark(s).", Severity.Success);
                _isEditingTags = false;
                _editTagsBuffer.Clear();
                _selectedResultIds.Clear();
            }
            else
            {
                Snackbar.Add("Failed to save tags.", Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error saving tags: {ex.Message}", Severity.Error);
        }
        finally
        {
            _rerunsLoading = false;
            StateHasChanged();
        }
    }
}
