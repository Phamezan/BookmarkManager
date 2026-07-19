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
    /// <summary>Tags each bookmark had immediately before this dialog's run started, so
    /// "Cancel Selected" can revert a bad test run instead of leaving live-saved tags in place.</summary>
    private Dictionary<Guid, List<string>> _preRunTagSnapshot = new();
    private HashSet<Guid> _selectedResultIds = [];
    private string _rerunStatusFilter = "All";
    private bool _rerunsLoading;
    private Dictionary<Guid, string> _editTagsBuffer = new();
    private List<ProviderTimingDto> _providerTimings = [];

    /// <summary>
    /// Accepted-but-not-yet-saved suggested titles from the Results &amp; Reruns table
    /// (per-row "Accept"). Applied together via "Apply title changes" through the same
    /// <see cref="BulkSaveTagsRequest.Titles"/> path the Review screen uses — never
    /// written server-side automatically (see <see cref="AiAutoTagBookmarkStatusDto.SuggestedTitle"/>).
    /// </summary>
    private Dictionary<Guid, string> _pendingTitleEdits = new();
    private Dictionary<Guid, string> _suggestionDrafts = new();

    private IReadOnlyList<string> RerunStatusOptions =>
        new[] { "All" }
            .Concat(_runResults.Select(r => r.Status).Distinct().OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            .ToList();

    private List<AiAutoTagBookmarkStatusDto> FilteredRunResults =>
        _rerunStatusFilter == "All"
            ? _runResults
            : _runResults.Where(r => r.Status == _rerunStatusFilter).ToList();

    private string GetSuggestionDraft(AiAutoTagBookmarkStatusDto item)
    {
        if (_suggestionDrafts.TryGetValue(item.BookmarkId, out var draft))
            return draft;
        return item.SuggestedTitle ?? string.Empty;
    }

    private void SetSuggestionDraft(Guid bookmarkId, string? value)
        => _suggestionDrafts[bookmarkId] = value ?? string.Empty;

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
        _preRunTagSnapshot.Clear();
        _selectedResultIds.Clear();
        _editTagsBuffer.Clear();
        _providerTimings.Clear();
        _pendingTitleEdits.Clear();
        _suggestionDrafts.Clear();
        _isEditingTags = false;
        _rerunStatusFilter = "All";
    }

    /// <summary>Per-row "Accept" on a suggested title — queues it for the "Apply title changes" button.</summary>
    private void AcceptSuggestedTitle(AiAutoTagBookmarkStatusDto item)
    {
        var draft = GetSuggestionDraft(item).Trim();
        if (draft.Length == 0) return;
        _pendingTitleEdits[item.BookmarkId] = draft;
        _suggestionDrafts.Remove(item.BookmarkId);
    }

    private void RevertPendingTitle(Guid bookmarkId)
    {
        _pendingTitleEdits.Remove(bookmarkId);
    }

    private async Task ApplyPendingTitleChangesAsync()
    {
        if (_pendingTitleEdits.Count == 0) return;

        _rerunsLoading = true;
        StateHasChanged();

        try
        {
            var request = new BulkSaveTagsRequest { Titles = new Dictionary<Guid, string>(_pendingTitleEdits) };
            var success = await BookmarkService.BulkSaveTagsAsync(request);

            if (success)
            {
                foreach (var (bookmarkId, newTitle) in _pendingTitleEdits)
                {
                    var index = _runResults.FindIndex(r => r.BookmarkId == bookmarkId);
                    if (index >= 0)
                    {
                        _runResults[index].Title = newTitle;
                    }
                }

                Snackbar.Add($"Applied {_pendingTitleEdits.Count} title change(s).", Severity.Success);
                _pendingTitleEdits.Clear();
            }
            else
            {
                Snackbar.Add("Failed to apply title changes.", Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error applying title changes: {ex.Message}", Severity.Error);
        }
        finally
        {
            _rerunsLoading = false;
            StateHasChanged();
        }
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
            await CaptureFolderTagSnapshotAsync(folderId);
            var excludedBookmarkIds = new HashSet<Guid>();
            var folderProcessed = 0;
            var totalTagged = 0;
            var totalAlreadyTagged = 0;
            var totalLowConfidence = 0;
            var totalNoSourceTags = 0;
            var totalFailedChunks = 0;
            var folderTotal = GetFolderCount(folderId);

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

    /// <summary>Snapshots each bookmark's current tags before this folder's run touches them,
    /// so a bad test run can be reverted per-bookmark via "Cancel Selected". Best-effort: if the
    /// fetch fails, tagging still proceeds — those bookmarks just won't be revertible.</summary>
    private async Task CaptureFolderTagSnapshotAsync(Guid folderId)
    {
        try
        {
            var bookmarks = await BookmarkService.GetBookmarksAsync(folderId);
            foreach (var bookmark in bookmarks.Where(b => b.Type == NodeType.Bookmark))
                _preRunTagSnapshot[bookmark.Id] = bookmark.Metadata?.Tags?.ToList() ?? [];
        }
        catch
        {
            // Best-effort — see summary doc comment above.
        }
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
            ForceRefresh = _forceRefresh,
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

    /// <summary>Reverts the checked bookmarks' tags to their pre-run snapshot (captured in
    /// <see cref="CaptureFolderTagSnapshotAsync"/>) and drops them from the results list —
    /// the "flawed test run, don't keep it" escape hatch. Bookmarks with no snapshot entry
    /// (e.g. this dialog session never captured them) revert to no tags rather than being
    /// left in their possibly-wrong tagged state.</summary>
    private async Task CancelSelectedResultsAsync()
    {
        if (_selectedResultIds.Count == 0)
            return;

        _rerunsLoading = true;
        StateHasChanged();

        try
        {
            var ids = _selectedResultIds.ToList();
            var tagsDict = ids.ToDictionary(
                id => id,
                id => _preRunTagSnapshot.TryGetValue(id, out var snapshot) ? snapshot : new List<string>());

            var success = await BookmarkService.BulkSaveTagsAsync(new BulkSaveTagsRequest { Tags = tagsDict });

            if (success)
            {
                foreach (var id in ids)
                {
                    _runResults.RemoveAll(r => r.BookmarkId == id);
                    _selectedResultIds.Remove(id);
                    _preRunTagSnapshot.Remove(id);
                }

                Snackbar.Add($"Reverted {ids.Count} bookmark(s) to their pre-run tags.", Severity.Success);
            }
            else
            {
                Snackbar.Add("Failed to cancel selected bookmarks.", Severity.Error);
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Error cancelling selected bookmarks: {ex.Message}", Severity.Error);
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
