using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services;

public class LinkCheckerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LinkCheckerService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Channel<bool> _triggerChannel = Channel.CreateUnbounded<bool>();
    private readonly object _statusLock = new();
    private bool _isRunning;
    private bool _rerunRequested;

    public bool IsRunning
    {
        get
        {
            lock (_statusLock)
            {
                return _isRunning;
            }
        }
    }

    public LinkCheckerService(
        IServiceScopeFactory scopeFactory,
        ILogger<LinkCheckerService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public void TriggerCheck()
    {
        lock (_statusLock)
        {
            if (_isRunning)
            {
                _rerunRequested = true;
                return;
            }
        }
        _triggerChannel.Writer.TryWrite(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Link checker background service started.");

        // Run daily check timer loop in background
        _ = RunDailyTimerAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await _triggerChannel.Reader.WaitToReadAsync(stoppingToken);
            while (_triggerChannel.Reader.TryRead(out _)) { }

            lock (_statusLock)
            {
                _isRunning = true;
                _rerunRequested = false;
            }

            try
            {
                await CheckLinksAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during link checking.");
            }
            finally
            {
                bool shouldRerun;
                lock (_statusLock)
                {
                    _isRunning = false;
                    shouldRerun = _rerunRequested;
                    _rerunRequested = false;
                }
                if (shouldRerun)
                {
                    TriggerCheck();
                }
            }
        }
    }

    private async Task RunDailyTimerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            TriggerCheck();
        }
    }

    private async Task CheckLinksAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting link check scan...");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var bookmarks = await db.BookmarkNodes
            .Where(n => n.Type == NodeType.Bookmark && !n.IsDeleted && n.Url != null)
            .ToListAsync(ct);

        if (bookmarks.Count == 0)
        {
            _logger.LogInformation("No active bookmarks to check.");
            return;
        }

        _logger.LogInformation("Checking {Count} bookmarks with concurrency limit 5...", bookmarks.Count);

        var httpClient = _httpClientFactory.CreateClient("LinkChecker");
        var semaphore = new SemaphoreSlim(5);
        var brokenIds = new List<Guid>();
        var failedIds = new List<Guid>();

        var tasks = bookmarks.Select(async bm =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                bool isBroken = await IsLinkBrokenAsync(httpClient, bm.Url!, ct);
                if (isBroken)
                {
                    lock (brokenIds)
                    {
                        brokenIds.Add(bm.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                // The check itself failed (our network, their timeout reset, etc.) —
                // leave this bookmark's previous flag untouched rather than guess.
                lock (failedIds)
                {
                    failedIds.Add(bm.Id);
                }
                _logger.LogWarning("Failed to check link {Url}: {Message}", bm.Url, ex.Message);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation("Finished checking links. Found {Count} broken links.", brokenIds.Count);

        // Report-only: flag bookmarks in place — no folder moves. The URL migrator
        // reads IsLinkBroken for its dead-domain candidates.
        var checkedAt = DateTime.UtcNow;
        var brokenSet = brokenIds.ToHashSet();
        var failedSet = failedIds.ToHashSet();
        foreach (var bm in bookmarks)
        {
            if (failedSet.Contains(bm.Id))
            {
                continue;
            }
            bm.IsLinkBroken = brokenSet.Contains(bm.Id);
            bm.LinkCheckedAt = checkedAt;
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> IsLinkBrokenAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                // Bot protection / auth walls / rate limits mean the site is alive.
                if (status is 401 or 403 or 429)
                {
                    return false;
                }
                // Cloudflare (and compatible WAFs) answering with a challenge — alive.
                if (response.Headers.Contains("cf-mitigated"))
                {
                    return false;
                }
                // Only confidently-dead statuses count as broken; 5xx and odd 4xx
                // are "unknown" and never flagged.
                return status is 404 or 410;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[32768]; // 32KB limit
            int totalBytesRead = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, totalBytesRead, buffer.Length - totalBytesRead, ct)) > 0)
            {
                totalBytesRead += bytesRead;
                if (totalBytesRead >= buffer.Length)
                {
                    break; // Cap reached
                }
            }

            // Small body implies domain parking / closed site
            if (totalBytesRead < 500)
            {
                return true;
            }

            var contentString = System.Text.Encoding.UTF8.GetString(buffer, 0, totalBytesRead).ToLowerInvariant();
            var failurePhrases = new[] 
            { 
                "domain is for sale", 
                "this domain may be for sale",
                "website is closed", 
                "account suspended", 
                "page not found",
                "404 not found"
            };

            if (failurePhrases.Any(p => contentString.Contains(p)))
            {
                return true;
            }

            return false;
        }
        catch (HttpRequestException)
        {
            return true;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Shutdown request, propagate.
        }
        catch (TaskCanceledException)
        {
            return true; // Timeout: treat as broken.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unknown exception checking link {Url}", url);
            return false; // Unknown is NOT broken.
        }
    }
}
