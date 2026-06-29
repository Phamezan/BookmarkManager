using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Infrastructure;
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
            // Find or create "Broken Links" folder
            var rootFolder = await db.BookmarkNodes
                .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && n.ParentId == null && !n.IsDeleted, ct);
            if (rootFolder == null)
            {
                rootFolder = await db.BookmarkNodes
                    .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && !n.IsDeleted, ct);
            }

            if (rootFolder == null)
            {
                _logger.LogWarning("Could not find any root folder to place Broken Links under.");
                return;
            }

            var brokenLinksFolder = await db.BookmarkNodes
                .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && n.ParentId == rootFolder.Id && n.Title == "Broken Links" && !n.IsDeleted, ct);

            if (brokenLinksFolder == null)
            {
                var maxPosRoot = await db.BookmarkNodes
                    .Where(n => n.ParentId == rootFolder.Id)
                    .MaxAsync(n => (int?)n.Position, ct) ?? -1;

                brokenLinksFolder = new BookmarkNode
                {
                    Id = Guid.NewGuid(),
                    ParentId = rootFolder.Id,
                    Type = NodeType.Folder,
                    Title = "Broken Links",
                    Position = maxPosRoot + 1,
                    SyncState = SyncState.Pending,
                    Version = 1,
                    UpdatedAt = DateTime.UtcNow
                };
                db.BookmarkNodes.Add(brokenLinksFolder);

                var createPayload = new
                {
                    parentId = rootFolder.BrowserNodeId ?? "1",
                    title = "Broken Links"
                };
                db.ExtensionCommands.Add(new ExtensionCommandEntry
                {
                    Id = Guid.NewGuid(),
                    OperationId = Guid.NewGuid(),
                    CommandType = "Create",
                    BookmarkId = brokenLinksFolder.Id,
                    BrowserNodeId = null,
                    ExpectedVersion = 0,
                    PayloadJson = JsonSerializer.Serialize(createPayload),
                    CreatedAt = DateTime.UtcNow,
                    Status = "Pending"
                });

                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Created new Broken Links folder in database, waiting for extension sync.");
            }

            // If Broken Links folder doesn't have a BrowserNodeId yet, we must wait for Brave to create it and report back.
            // Only move bookmarks if the BrowserNodeId exists!
            if (!string.IsNullOrEmpty(brokenLinksFolder.BrowserNodeId))
            {
                var brokenBookmarks = await db.BookmarkNodes
                    .Where(n => brokenIds.Contains(n.Id) && n.ParentId != brokenLinksFolder.Id)
                    .ToListAsync(ct);

                if (brokenBookmarks.Count > 0)
                {
                    var maxPosBroken = await db.BookmarkNodes
                        .Where(n => n.ParentId == brokenLinksFolder.Id)
                        .MaxAsync(n => (int?)n.Position, ct) ?? -1;

                    int nextPos = maxPosBroken + 1;
                    foreach (var bm in brokenBookmarks)
                    {
                        bm.ParentId = brokenLinksFolder.Id;
                        bm.Position = nextPos++;
                        bm.SyncState = SyncState.Pending;
                        bm.UpdatedAt = DateTime.UtcNow;

                        var movePayload = new
                        {
                            parentBrowserNodeId = brokenLinksFolder.BrowserNodeId,
                            position = bm.Position
                        };

                        db.ExtensionCommands.Add(new ExtensionCommandEntry
                        {
                            Id = Guid.NewGuid(),
                            OperationId = Guid.NewGuid(),
                            CommandType = "Move",
                            BookmarkId = bm.Id,
                            BrowserNodeId = bm.BrowserNodeId,
                            ExpectedVersion = bm.Version,
                            PayloadJson = JsonSerializer.Serialize(movePayload),
                            CreatedAt = DateTime.UtcNow,
                            Status = "Pending"
                        });
                    }

                    await db.SaveChangesAsync(ct);
                    await SyncWebSocketManager.BroadcastSyncAsync();
                    _logger.LogInformation("Moved {Count} broken bookmarks to Broken Links folder.", brokenBookmarks.Count);
                }
            }
            else
            {
                _logger.LogWarning("Broken Links folder has no BrowserNodeId yet. Deferring bookmark movements until next scan/sync.");
            }
        }
    }

    private async Task<bool> IsLinkBrokenAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await client.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return true;
            }

            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                request = new HttpRequestMessage(HttpMethod.Get, url);
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return true;
                }
            }

            return !response.IsSuccessStatusCode;
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
