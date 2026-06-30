using System.Threading.Channels;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services;

public sealed class AutoTaggerBackgroundJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutoTaggerBackgroundJob> _logger;
    private readonly Channel<bool> _triggerChannel = Channel.CreateUnbounded<bool>();
    private readonly object _statusLock = new();
    private bool _isRunning;
    private DateTime? _lastStartedAt;
    private DateTime? _lastCompletedAt;
    private string? _lastError;
    private RetagAllResult _lastResult = new();

    public AutoTaggerBackgroundJob(IServiceScopeFactory scopeFactory, ILogger<AutoTaggerBackgroundJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Trigger()
    {
        _triggerChannel.Writer.TryWrite(true);
    }

    public AutoTaggerStatusDto GetStatus()
    {
        lock (_statusLock)
        {
            return new AutoTaggerStatusDto
            {
                IsRunning = _isRunning,
                LastStartedAt = _lastStartedAt,
                LastCompletedAt = _lastCompletedAt,
                LastError = _lastError,
                LastResult = new RetagAllResult
                {
                    Tagged = _lastResult.Tagged,
                    Skipped = _lastResult.Skipped,
                    Total = _lastResult.Total
                }
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Auto tagger background job started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await _triggerChannel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false);
            while (_triggerChannel.Reader.TryRead(out _)) { }

            if (GetStatus().IsRunning)
                continue;

            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        lock (_statusLock)
        {
            _isRunning = true;
            _lastStartedAt = DateTime.UtcNow;
            _lastError = null;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var autoTagger = scope.ServiceProvider.GetRequiredService<AutoTaggerService>();
            var result = await autoTagger.ProcessUntaggedAsync(ct).ConfigureAwait(false);

            lock (_statusLock)
            {
                _lastResult = result;
                _lastCompletedAt = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto tagger background run failed.");
            lock (_statusLock)
            {
                _lastError = ex.Message;
                _lastCompletedAt = DateTime.UtcNow;
            }
        }
        finally
        {
            lock (_statusLock)
            {
                _isRunning = false;
            }
        }
    }
}
