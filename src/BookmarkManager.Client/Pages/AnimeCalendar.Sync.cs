using BookmarkManager.Client.Services;
using Microsoft.AspNetCore.Components;

namespace BookmarkManager.Client.Pages;

public partial class AnimeCalendar
{
    // The server broadcasts one "sync" per changed bookmark, so bulk operations (auto-tagger,
    // link checker, snapshot import) arrive as a burst. Waiting out a short quiet period lets
    // the burst collapse into a single schedule sweep instead of one per message.
    private static readonly TimeSpan SyncQuietPeriod = TimeSpan.FromSeconds(1);

    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private SyncSocketListener SyncSocketListener { get; set; } = default!;

    private CancellationTokenSource? _wsCts;
    private SyncReloadCoalescer? _syncCoalescer;

    [Inject] private ILogger<AnimeCalendar> Logger { get; set; } = default!;

    private void StartWebSocketListener()
    {
        _wsCts?.Cancel();
        _wsCts?.Dispose();
        _wsCts = new CancellationTokenSource();
        _syncCoalescer = new SyncReloadCoalescer(ReloadForSyncAsync, SyncQuietPeriod);
        _ = ListenForSyncEventsAsync(_wsCts.Token);
    }

    private async Task ReloadForSyncAsync(CancellationToken ct)
    {
        try
        {
            await LoadScheduleAndAutoMatchAsync(onlyNewSinceLastLoad: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during sync-triggered schedule reload");
        }
    }

    private async Task ListenForSyncEventsAsync(CancellationToken ct)
    {
        await SyncSocketListener.ListenAsync(async () =>
        {
            if (_selectedFolderIds.Count > 0 && _syncCoalescer is not null)
            {
                var coalescer = _syncCoalescer;
                await InvokeAsync(() => _ = coalescer.SignalAsync(ct));
            }
        }, ct);
    }

    public void Dispose()
    {
        _wsCts?.Cancel();
        _wsCts?.Dispose();
    }
}
