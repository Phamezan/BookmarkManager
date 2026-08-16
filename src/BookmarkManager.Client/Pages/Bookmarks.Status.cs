using BookmarkManager.Client.Components;
using BookmarkManager.Contracts;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private async Task ChangeBookmarkStatusAsync(BookmarkNodeDto bookmark, string newStatus)
    {
        CloseContextMenu();
        var normStatus = BookmarkReadingStatus.Normalize(newStatus);
        if (normStatus is null)
            return;

        try
        {
            var updated = await BookmarkService.UpdateBookmarkStatusAsync(bookmark.Id, normStatus);
            if (updated is not null)
            {
                // Update local model
                bookmark.Metadata ??= new BookmarkMetadataDto();
                bookmark.Metadata.Status = normStatus;
                bookmark.ParentId = updated.ParentId;
                bookmark.Position = updated.Position;
                bookmark.SyncState = updated.SyncState;
                InvalidateVisibleItemsCache();
                Snackbar.Add($"Status updated to '{normStatus}'", Severity.Success);

                // Check for related series candidates to offer review
                try
                {
                    var preview = await BookmarkService.GetRelatedStatusCandidatesAsync(bookmark.Id, normStatus);
                    if (preview.Candidates is { Count: > 0 })
                    {
                        var parameters = new DialogParameters<RelatedSeriesDialog>
                        {
                            { x => x.SourceBookmarkId, bookmark.Id },
                            { x => x.SourceTitle, bookmark.Title },
                            { x => x.TargetStatus, normStatus },
                            { x => x.InitialCandidates, preview.Candidates }
                        };

                        var options = new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true, CloseButton = true };
                        var dialog = await DialogService.ShowAsync<RelatedSeriesDialog>("Review Related Series", parameters, options);
                        var result = await dialog.Result;
                        if (result is not null && !result.Canceled && result.Data is int count && count > 0)
                        {
                            Snackbar.Add($"Updated {count} related bookmarks to '{normStatus}'", Severity.Success);
                            if (_selectedFolderId.HasValue)
                            {
                                _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
                                InvalidateVisibleItemsCache();
                            }
                        }
                    }
                }
                catch
                {
                    // Candidate preview is an optional convenience, ignore background errors
                }
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to update status: {ex.Message}", Severity.Error);
        }
    }

    private async Task ChangeBulkStatusAsync(string newStatus)
    {
        var ids = _selectedBookmarkIds.ToList();
        if (ids.Count == 0)
            return;

        var normStatus = BookmarkReadingStatus.Normalize(newStatus);
        if (normStatus is null)
            return;

        try
        {
            var response = await BookmarkService.BulkUpdateBookmarkStatusAsync(new BulkUpdateBookmarkStatusRequest
            {
                BookmarkIds = ids,
                Status = normStatus
            });

            var parts = new List<string>();
            if (response.ChangedCount > 0)
                parts.Add($"{response.ChangedCount} updated");
            if (response.AlreadyCorrectCount > 0)
                parts.Add($"{response.AlreadyCorrectCount} already in '{BookmarkReadingStatus.GetDisplayLabel(normStatus)}'");
            if (response.SkippedCount > 0)
                parts.Add($"{response.SkippedCount} skipped");
            if (response.FailedCount > 0)
                parts.Add($"{response.FailedCount} failed");

            var message = parts.Count > 0
                ? string.Join(", ", parts)
                : "No bookmarks were updated";

            var severity = response.FailedCount > 0
                ? (response.ChangedCount > 0 ? Severity.Warning : Severity.Error)
                : (response.ChangedCount > 0 ? Severity.Success : Severity.Info);

            Snackbar.Add(message, severity);

            ClearSelectionAndAnchor();

            if (_selectedFolderId.HasValue)
            {
                _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value);
                InvalidateVisibleItemsCache();
            }
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Failed to update status on selected bookmarks: {ex.Message}", Severity.Error);
        }
    }
}
