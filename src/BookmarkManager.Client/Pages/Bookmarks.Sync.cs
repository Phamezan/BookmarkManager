using BookmarkManager.Client.Components;
using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using MudBlazor;

namespace BookmarkManager.Client.Pages;

public partial class Bookmarks
{
    private async Task StartWebSocketListenerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var webSocket = new System.Net.WebSockets.ClientWebSocket();
            var uri = new Uri(NavigationManager.ToAbsoluteUri("api/sync/ws").ToString().Replace("http://", "ws://").Replace("https://", "wss://"));
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
                        if (msg == "sync")
                        {
                            await InvokeAsync(async () =>
                            {
                                try
                                {
                                    _folderTree = await BookmarkService.GetFolderTreeAsync(ct);
                                    await LoadFavoritesAsync();

                                    if (!string.IsNullOrWhiteSpace(_searchQuery))
                                    {
                                        _items = (await BookmarkService.SearchBookmarksAsync(new SearchRequest { Query = _searchQuery }, ct)).Items;
                                        await LoadTagsAsync();
                                        StateHasChanged();
                                        return;
                                    }

                                    // If the selected folder no longer exists in the new tree (e.g. after a restore),
                                    // fall back to the first available folder to avoid loading stale/empty data.
                                    var allFolderIds = CollectAllFolderIds(_folderTree);
                                    if (_selectedFolderId == null || !allFolderIds.Contains(_selectedFolderId.Value))
                                    {
                                        if (_folderTree.Count > 0)
                                        {
                                            var firstId = FindFirstLeaf(_folderTree[0]);
                                            if (firstId != Guid.Empty)
                                                await OnFolderSelected(firstId);
                                        }
                                        else
                                        {
                                            _selectedFolderId = null;
                                            _items = [];
                                        }
                                    }
                                    else
                                    {
                                        _items = await BookmarkService.GetBookmarksAsync(_selectedFolderId.Value, ct);
                                        await LoadTagsAsync();
                                    }
                                    StateHasChanged();
                                }
                                catch
                                {
                                    // Ignore network/API fetch errors
                                }
                            });
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

}
