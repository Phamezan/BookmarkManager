using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace BookmarkManager.Api.Infrastructure;

public static class SyncWebSocketManager
{
    private static readonly ConcurrentDictionary<Guid, WebSocket> Clients = new();

    public static async Task HandleConnectionAsync(WebSocket webSocket)
    {
        var id = Guid.NewGuid();
        Clients.TryAdd(id, webSocket);
        
        var buffer = new byte[1024 * 4];
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
        }
        catch
        {
            // Ignore socket errors (disconnections)
        }
        finally
        {
            Clients.TryRemove(id, out _);
        }
    }

    public static async Task BroadcastSyncAsync()
    {
        var msg = Encoding.UTF8.GetBytes("sync");
        var segment = new ArraySegment<byte>(msg);
        foreach (var client in Clients.Values)
        {
            if (client.State == WebSocketState.Open)
            {
                try
                {
                    await client.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch
                {
                    // Ignore send errors on dead sockets
                }
            }
        }
    }
}
