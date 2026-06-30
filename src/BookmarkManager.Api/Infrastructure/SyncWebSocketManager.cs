using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

namespace BookmarkManager.Api.Infrastructure;

public static class SyncWebSocketManager
{
    private static readonly ConcurrentDictionary<Guid, WebSocket> Clients = new();

    public static async Task HandleConnectionAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        Clients.TryAdd(id, webSocket);
        
        var buffer = new byte[1024 * 4];
        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown or client disconnect.
        }
        catch
        {
            // Ignore socket errors (disconnections)
        }
        finally
        {
            Clients.TryRemove(id, out _);

            // Close the socket promptly on shutdown/disconnect so Kestrel does not
            // wait on a dangling connection for the full shutdown timeout.
            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Server shutting down",
                        CancellationToken.None);
                }
                catch
                {
                    // Best effort; the socket may already be gone.
                }
            }
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

    public static void CloseAll()
    {
        foreach (var client in Clients.Values)
        {
            try
            {
                client.Abort();
            }
            catch
            {
                // Best effort
            }
        }
    }
}
