using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public interface IExtensionConnectionService
{
    bool IsConnected { get; }
    event Action? ConnectionStateChanged;
    Task PollAsync(CancellationToken cancellationToken = default);
}

public sealed class ExtensionConnectionService : IExtensionConnectionService, IDisposable
{
    private readonly IBookmarkManagerApiClient _apiClient;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollingTask;

    public bool IsConnected { get; private set; }
    public event Action? ConnectionStateChanged;

    public ExtensionConnectionService(IBookmarkManagerApiClient apiClient)
    {
        _apiClient = apiClient;
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        _pollingTask = PollLoopAsync();
    }

    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _apiClient.GetAsync<ExtensionStatusDto>("/api/extension/status", cancellationToken);
            var wasConnected = IsConnected;
            IsConnected = status?.IsConnected ?? false;

            if (wasConnected != IsConnected)
            {
                ConnectionStateChanged?.Invoke();
            }
        }
        catch
        {
            var wasConnected = IsConnected;
            IsConnected = false;

            if (wasConnected)
            {
                ConnectionStateChanged?.Invoke();
            }
        }
    }

    private async Task PollLoopAsync()
    {
        await PollAsync(_cts.Token);
        while (await _timer.WaitForNextTickAsync(_cts.Token))
        {
            await PollAsync(_cts.Token);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _timer.Dispose();
    }
}
