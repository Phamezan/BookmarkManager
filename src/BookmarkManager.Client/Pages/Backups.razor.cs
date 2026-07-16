using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Backups
{
    private const long ChartMaxBytes = 4_000_000;

    [Inject] private IBackupService BackupService { get; set; } = default!;
    [Inject] private ISnackbar Snackbar { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;

    private BackupStatsDto? _stats;
    private IReadOnlyList<BackupManifestDto> _history = [];
    private bool _loading = true;
    private bool _creating;
    private BackupManifestDto? _restoreTarget;
    private string _restoreConfirm = string.Empty;
    private bool _restoring;
    private bool _restoreRestartPending;
    private BackupActivityDayDto? _hoveredDay;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _stats = await BackupService.GetStatsAsync();
            _history = await BackupService.GetBackupsAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Could not load backups: {ex.Message}", Severity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task CreateBackupAsync()
    {
        if (_creating)
        {
            return;
        }

        _creating = true;
        try
        {
            await BackupService.CreateBackupAsync();
            Snackbar.Add("Backup completed.", Severity.Success);
            await LoadAsync();
        }
        catch (ApiConflictException)
        {
            Snackbar.Add("A backup is already running.", Severity.Warning);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Backup failed: {ex.Message}", Severity.Error);
            await LoadAsync();
        }
        finally
        {
            _creating = false;
        }
    }

    private async Task DeleteBackupAsync(BackupManifestDto manifest)
    {
        var dialog = await DialogService.ShowAsync<ConfirmDialog>("Delete backup", new DialogParameters
        {
            ["Message"] = $"Delete {manifest.Name}? This cannot be undone.",
            ["ConfirmText"] = "Delete",
            ["CancelText"] = "Cancel"
        });
        var result = await dialog.Result;
        if (result?.Canceled != false)
        {
            return;
        }

        try
        {
            await BackupService.DeleteBackupAsync(manifest.Id);
            Snackbar.Add("Backup deleted.", Severity.Success);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Delete failed: {ex.Message}", Severity.Error);
        }
    }

    private void OpenRestoreDialog(BackupManifestDto manifest)
    {
        if (_restoring || _restoreRestartPending)
        {
            return;
        }

        _restoreTarget = manifest;
        _restoreConfirm = string.Empty;
    }

    private void CloseRestoreDialog()
    {
        if (_restoring)
        {
            return;
        }

        _restoreTarget = null;
        _restoreConfirm = string.Empty;
    }

    private async Task ConfirmRestoreAsync()
    {
        if (_restoreTarget is null || _restoreConfirm != "RESTORE" || _restoring)
        {
            return;
        }

        _restoring = true;
        try
        {
            var result = await BackupService.RestoreAsync(_restoreTarget.Id, _restoreConfirm);
            _restoreTarget = null;
            _restoreConfirm = string.Empty;
            _restoreRestartPending = true;
            Snackbar.Add(
                result.RestartRequired
                    ? "Restore staged. Restarting API — reconnect when the server is back."
                    : result.Message,
                Severity.Warning);
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            Snackbar.Add("Type RESTORE exactly to confirm.", Severity.Warning);
        }
        catch (ApiConflictException)
        {
            Snackbar.Add("A backup or restore is already in progress.", Severity.Warning);
        }
        catch (Exception ex)
        {
            Snackbar.Add($"Restore failed: {ex.Message}", Severity.Error);
        }
        finally
        {
            _restoring = false;
        }
    }

    private static string FormatRelative(DateTime? utc)
    {
        if (utc is null)
        {
            return "never";
        }

        var delta = DateTime.UtcNow - utc.Value;
        if (delta.TotalMinutes < 1)
        {
            return "just now";
        }

        if (delta.TotalHours < 1)
        {
            return $"{(int)delta.TotalMinutes}m ago";
        }

        if (delta.TotalDays < 1)
        {
            return $"{(int)delta.TotalHours}h ago";
        }

        return $"{(int)delta.TotalDays}d ago";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_000_000)
        {
            return $"{bytes / 1_000_000d:0.#} MB";
        }

        if (bytes >= 1_000)
        {
            return $"{bytes / 1_000d:0.#} KB";
        }

        return $"{bytes} B";
    }

    private static string FormatDuration(long durationMs)
    {
        if (durationMs < 1000)
        {
            return $"{durationMs} ms";
        }

        return $"{durationMs / 1000d:0.#} s";
    }

    private static string ContentsLine(BackupManifestDto manifest)
        => $"{manifest.BookmarkCount:N0} bookmarks · {manifest.FolderCount:N0} folders · {manifest.TagCount:N0} tags · {manifest.LibraryTitleCount:N0} library titles";

    private static string ContentsLine(BackupStatsDto stats)
    {
        var last = stats.LastBackup;
        return last is null
            ? "No snapshot contents yet"
            : ContentsLine(last);
    }

    private static double BarHeightPx(BackupActivityDayDto day)
    {
        if (day.Status == "Failed")
        {
            return 4;
        }

        if (day.SizeBytes <= 0)
        {
            return 0;
        }

        return Math.Min(24, Math.Max(2, day.SizeBytes / (double)ChartMaxBytes * 24));
    }

    private static double RingOffset(double successRate)
    {
        const double circumference = 2 * Math.PI * 52;
        return circumference * (1 - successRate / 100d);
    }

    private BackupActivityDayDto? LatestFailedDay()
        => _stats?.Activity.LastOrDefault(day => day.Status == "Failed");
}
