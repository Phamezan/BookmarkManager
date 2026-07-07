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
    private readonly Channel<bool> _triggerChannel = Channel.CreateUnbounded<bool>();
    private bool _isRunning;

    public bool IsRunning => _isRunning;

    public LinkCheckerService(IServiceScopeFactory scopeFactory, ILogger<LinkCheckerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void TriggerCheck()
    {
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

            if (_isRunning) continue;

            _isRunning = true;
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
                _isRunning = false;
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

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        var semaphore = new SemaphoreSlim(5);
        var brokenIds = new List<Guid>();

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
                _logger.LogWarning("Failed to check link {Url}: {Message}", bm.Url, ex.Message);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation("Finished checking links. Found {Count} broken links.", brokenIds.Count);

        if (brokenIds.Count > 0)
        {
            var brokenLinksFolder = await BrokenLinksFolderHelper.GetOrCreateFolderAsync(db, _logger, ct);
            if (brokenLinksFolder == null)
            {
                return;
            }

            await BrokenLinksFolderHelper.MoveBookmarksIntoFolderAsync(db, brokenLinksFolder, brokenIds, _logger, ct);
        }
    }

    private async Task<bool> IsLinkBrokenAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                return true;
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
        catch (TaskCanceledException)
        {
            return true;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
