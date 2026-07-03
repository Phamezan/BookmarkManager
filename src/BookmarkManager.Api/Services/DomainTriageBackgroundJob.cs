using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services;

public record TriageJobStatus(
    bool IsRunning,
    int TotalFound,
    int SuccessfullyProcessed,
    string? TargetFolder,
    string? CurrentDomain,
    string? ErrorMessage
);

public sealed class DomainTriageBackgroundJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DomainTriageBackgroundJob> _logger;
    private readonly Channel<TriageDomainRequest> _requestChannel = Channel.CreateUnbounded<TriageDomainRequest>();
    
    private readonly object _statusLock = new();
    private bool _isRunning;
    private int _totalFound;
    private int _successfullyProcessed;
    private string? _targetFolder;
    private string? _currentDomain;
    private string? _errorMessage;

    public DomainTriageBackgroundJob(IServiceScopeFactory scopeFactory, ILogger<DomainTriageBackgroundJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool Enqueue(TriageDomainRequest request)
    {
        lock (_statusLock)
        {
            if (_isRunning) return false;
            
            _isRunning = true;
            _totalFound = 0;
            _successfullyProcessed = 0;
            _targetFolder = request.FolderName;
            _currentDomain = request.MatchBaseUrl;
            _errorMessage = null;
        }

        var queued = _requestChannel.Writer.TryWrite(request);
        if (!queued)
        {
            lock (_statusLock)
            {
                _isRunning = false;
            }
        }
        return queued;
    }

    public TriageJobStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new TriageJobStatus(
                _isRunning,
                _totalFound,
                _successfullyProcessed,
                _targetFolder,
                _currentDomain,
                _errorMessage
            );
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Domain triage background job started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var request = await _requestChannel.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                
                try
                {
                    await RunTriageAsync(request, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Domain triage execution failed.");
                    lock (_statusLock)
                    {
                        _errorMessage = ex.Message;
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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Domain triage background job loop encountered an error.");
            }
        }
    }

    private async Task RunTriageAsync(TriageDomainRequest request, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var searchService = scope.ServiceProvider.GetRequiredService<IDuckDuckGoSearchService>();

        var bookmarks = await db.BookmarkNodes
            .Where(n => n.Type == NodeType.Bookmark && !n.IsDeleted && n.Url != null)
            .ToListAsync(ct);

        var matchedBookmarks = bookmarks
            .Where(n => n.Url!.StartsWith(request.MatchBaseUrl, StringComparison.OrdinalIgnoreCase) || 
                        n.Url!.Contains(request.MatchBaseUrl, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchedBookmarks.Count == 0)
        {
            return;
        }

        lock (_statusLock)
        {
            _totalFound = matchedBookmarks.Count;
        }

        var rootFolder = await db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && n.ParentId == null && !n.IsDeleted, ct);
        if (rootFolder == null)
        {
            rootFolder = await db.BookmarkNodes
                .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && !n.IsDeleted, ct);
        }

        if (rootFolder == null)
        {
            throw new InvalidOperationException("Root folder not found in database.");
        }

        var folderName = string.IsNullOrWhiteSpace(request.FolderName) 
            ? $"Fix - {ExtractDomain(request.MatchBaseUrl)}" 
            : request.FolderName;

        lock (_statusLock)
        {
            _targetFolder = folderName;
        }

        var triageFolder = await db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.Type == NodeType.Folder && n.ParentId == rootFolder.Id && n.Title == folderName && !n.IsDeleted, ct);

        if (triageFolder == null)
        {
            var maxPosRoot = await db.BookmarkNodes
                .Where(n => n.ParentId == rootFolder.Id)
                .MaxAsync(n => (int?)n.Position, ct) ?? -1;

            triageFolder = new BookmarkNode
            {
                Id = Guid.NewGuid(),
                ParentId = rootFolder.Id,
                Type = NodeType.Folder,
                Title = folderName,
                Position = maxPosRoot + 1,
                SyncState = SyncState.Pending,
                Version = 1,
                UpdatedAt = DateTime.UtcNow
            };
            db.BookmarkNodes.Add(triageFolder);

            var createPayload = new
            {
                parentId = rootFolder.BrowserNodeId ?? "1",
                title = folderName
            };
            db.ExtensionCommands.Add(new ExtensionCommandEntry
            {
                Id = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                CommandType = "Create",
                BookmarkId = triageFolder.Id,
                BrowserNodeId = null,
                ExpectedVersion = 0,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(createPayload),
                CreatedAt = DateTime.UtcNow,
                Status = "Pending"
            });

            await db.SaveChangesAsync(ct);
        }

        var deadDomain = ExtractDomain(request.MatchBaseUrl);

        var maxPosTriage = await db.BookmarkNodes
            .Where(n => n.ParentId == triageFolder.Id)
            .MaxAsync(n => (int?)n.Position, ct) ?? -1;

        int nextPosition = maxPosTriage + 1;

        foreach (var bm in matchedBookmarks)
        {
            ct.ThrowIfCancellationRequested();

            bool urlUpdated = false;
            string? newUrl = null;

            if (request.ActionType.Equals("AutoSearch", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    newUrl = await searchService.FindAlternativeUrlAsync(bm.Title, bm.Category, deadDomain, ct);
                    if (!string.IsNullOrEmpty(newUrl))
                    {
                        bm.Url = newUrl;
                        bm.Version++;
                        urlUpdated = true;
                    }
                    else
                    {
                        var searchNote = $"[Triage System] DuckDuckGo search returned no alternative links on {DateTime.UtcNow:g} UTC.";
                        bm.Notes = string.IsNullOrWhiteSpace(bm.Notes) 
                            ? searchNote 
                            : $"{bm.Notes}\n{searchNote}";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to search alternative URL for bookmark {BookmarkId}", bm.Id);
                    var searchNote = $"[Triage System] Search failed: {ex.Message}";
                    bm.Notes = string.IsNullOrWhiteSpace(bm.Notes) 
                        ? searchNote 
                        : $"{bm.Notes}\n{searchNote}";
                }
            }

            bm.ParentId = triageFolder.Id;
            bm.Position = nextPosition++;
            bm.SyncState = SyncState.Pending;
            bm.UpdatedAt = DateTime.UtcNow;

            if (urlUpdated && !string.IsNullOrEmpty(bm.BrowserNodeId))
            {
                var updatePayload = new
                {
                    title = bm.Title,
                    url = bm.Url
                };
                db.ExtensionCommands.Add(new ExtensionCommandEntry
                {
                    Id = Guid.NewGuid(),
                    OperationId = Guid.NewGuid(),
                    CommandType = "Update",
                    BookmarkId = bm.Id,
                    BrowserNodeId = bm.BrowserNodeId,
                    ExpectedVersion = bm.Version - 1,
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(updatePayload),
                    CreatedAt = DateTime.UtcNow,
                    Status = "Pending"
                });
            }

            if (!string.IsNullOrEmpty(triageFolder.BrowserNodeId) && !string.IsNullOrEmpty(bm.BrowserNodeId))
            {
                bm.ParentBrowserNodeId = triageFolder.BrowserNodeId;
                
                var movePayload = new
                {
                    parentBrowserNodeId = triageFolder.BrowserNodeId,
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
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(movePayload),
                    CreatedAt = DateTime.UtcNow,
                    Status = "Pending"
                });
            }

            lock (_statusLock)
            {
                _successfullyProcessed++;
            }
        }

        await db.SaveChangesAsync(ct);
        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();
    }

    private static string ExtractDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        try
        {
            var uri = new Uri(url);
            return uri.Host;
        }
        catch
        {
            var clean = url.Replace("https://", "").Replace("http://", "");
            var idx = clean.IndexOf('/');
            if (idx >= 0) clean = clean.Substring(0, idx);
            return clean;
        }
    }
}
