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

    private CancellationTokenSource? _wsCts;
    private SyncReloadCoalescer? _syncCoalescer;

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
        catch
        {
            // Ignore transient reload failures - the next sync event will retry.
        }
    }

    private async Task ListenForSyncEventsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var webSocket = new System.Net.WebSockets.ClientWebSocket();
            var uri = new Uri(NavigationManager.ToAbsoluteUri("api/sync/ws").ToString()
                .Replace("http://", "ws://").Replace("https://", "wss://"));
            try
            {
                await webSocket.ConnectAsync(uri, ct);
                var buffer = new byte[1024 * 4];
                while (webSocket.State == System.Net.WebSockets.WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    {
                        break;
                    }
                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                    {
                        var msg = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (msg == "sync" && _selectedFolderIds.Count > 0 && _syncCoalescer is not null)
                        {
                            // Fire-and-forget: SignalAsync returns immediately when a cycle is
                            // already running, and the receive loop must not block behind the
                            // quiet period + reload of the first signal in a burst.
                            var coalescer = _syncCoalescer;
                            await InvokeAsync(() => _ = coalescer.SignalAsync(ct));
                        }
                    }
                }
            }
            catch
            {
                try
                {
                    await Task.Delay(2000, ct);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        _wsCts?.Cancel();
        _wsCts?.Dispose();
    }
}
