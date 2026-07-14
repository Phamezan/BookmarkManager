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
    }

    private void RefreshEta()
        => _etaText = AutoTagProgressEstimator.EstimateRemaining(_taggingStartedAt, _processedCount, _totalCount);
}
