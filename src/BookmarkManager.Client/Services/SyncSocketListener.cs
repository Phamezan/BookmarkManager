using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Client.Services;

public class SyncSocketListener
{
    private readonly NavigationManager _navigationManager;
    private readonly ILogger<SyncSocketListener> _logger;

    public SyncSocketListener(NavigationManager navigationManager, ILogger<SyncSocketListener> logger)
    {
        _navigationManager = navigationManager;
        _logger = logger;
    }

    public async Task ListenAsync(Func<Task> onSync, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            using var webSocket = new ClientWebSocket();
            var uriString = _navigationManager.ToAbsoluteUri("api/sync/ws").ToString()
                .Replace("http://", "ws://")
                .Replace("https://", "wss://");
            var uri = new Uri(uriString);

            try
            {
                await webSocket.ConnectAsync(uri, ct);
                attempt = 0; // reset backoff on successful connect
                var buffer = new byte[1024 * 4];

                while (webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (msg == "sync")
                        {
                            await onSync();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebSocket sync listener connection failed; retrying...");
                
                // Exponential backoff: min(2000 * 2^attempt, 30000)
                var delayMs = Math.Min(2000 * (int)Math.Pow(2, attempt), 30000);
                attempt++;
                
                try
                {
                    await Task.Delay(delayMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
