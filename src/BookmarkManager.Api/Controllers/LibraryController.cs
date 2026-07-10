using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/library")]
public sealed class LibraryController(
    LibrarySearchService searchService,
    AppDbContext db,
    IMapper mapper,
    LibraryProviderRegistry registry,
    ReleaseWatcherBackgroundService watcher,
    LibraryCatalogSyncBackgroundService catalogSync) : ControllerBase
{
    [HttpGet("search")]
    public async Task<ActionResult<LibrarySearchResponse>> Search(
        [FromQuery] string q,
        [FromQuery] LibraryMediaType? type,
        [FromQuery] string? providers,
        CancellationToken cancellationToken)
    {
        var request = new LibrarySearchRequest
        {
            Query = q ?? string.Empty,
            MediaType = type,
            Providers = string.IsNullOrWhiteSpace(providers)
                ? null
                : providers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        };

        var response = await searchService.SearchAsync(request, cancellationToken).ConfigureAwait(false);
        return Ok(response);
    }

    [HttpGet("trending")]
    public async Task<ActionResult<LibrarySearchResponse>> Trending(
        [FromQuery] LibraryMediaType? type,
        [FromQuery] int skip,
        [FromQuery] int take,
        CancellationToken cancellationToken)
    {
        var response = await searchService.GetTrendingAsync(type, skip, take <= 0 ? 48 : take, cancellationToken).ConfigureAwait(false);
        return Ok(response);
    }

    [HttpGet("catalog/status")]
    public async Task<ActionResult<LibraryCatalogSyncStatusDto>> GetCatalogSyncStatus(CancellationToken cancellationToken)
    {
        return Ok(await catalogSync.GetStatusAsync(cancellationToken).ConfigureAwait(false));
    }

    [HttpPost("catalog/sync")]
    public IActionResult TriggerCatalogResync()
    {
        catalogSync.TriggerFullResync();
        return Accepted();
    }

    [HttpGet("tracked")]
    public async Task<ActionResult<List<TrackedSeriesDto>>> GetTracked(CancellationToken cancellationToken)
    {
        var tracked = await db.TrackedSeries
            .Include(ts => ts.Bookmark)
            .Where(ts => !ts.Bookmark.IsDeleted)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(mapper.Map<List<TrackedSeriesDto>>(tracked));
    }

    [HttpPost("track")]
    public async Task<ActionResult<BookmarkNodeDto>> Track(
        [FromBody] TrackLibraryEntryRequest request,
        CancellationToken cancellationToken)
    {
        var existing = await db.TrackedSeries
            .Include(ts => ts.Bookmark)
            .FirstOrDefaultAsync(
                ts => ts.Provider == request.Provider && ts.ProviderId == request.ProviderId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null && !existing.Bookmark.IsDeleted)
        {
            return Ok(mapper.Map<BookmarkNodeDto>(existing.Bookmark));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string parentBrowserNodeId = "0";
            BookmarkNode? parentNode = null;
            if (request.ParentId != Guid.Empty)
            {
                parentNode = await db.BookmarkNodes
                    .FirstOrDefaultAsync(n => n.Id == request.ParentId && !n.IsDeleted, cancellationToken)
                    .ConfigureAwait(false);
                if (parentNode is null)
                {
                    return NotFound("Parent folder not found");
                }
                parentBrowserNodeId = parentNode.BrowserNodeId ?? "0";
            }

            var maxPos = request.ParentId == Guid.Empty
                ? await db.BookmarkNodes.Where(n => n.ParentId == null).MaxAsync(n => (int?)n.Position, cancellationToken).ConfigureAwait(false) ?? -1
                : await db.BookmarkNodes.Where(n => n.ParentId == request.ParentId).MaxAsync(n => (int?)n.Position, cancellationToken).ConfigureAwait(false) ?? -1;

            var bookmarkNode = existing?.Bookmark ?? new BookmarkNode
            {
                Id = Guid.NewGuid(),
                Type = NodeType.Bookmark,
                Version = 1,
            };

            bookmarkNode.ParentId = request.ParentId == Guid.Empty ? null : request.ParentId;
            bookmarkNode.Title = request.Title;
            bookmarkNode.Url = request.SourceUrl;
            bookmarkNode.Position = maxPos + 1;
            bookmarkNode.SyncState = SyncState.Pending;
            bookmarkNode.UpdatedAt = DateTime.UtcNow;
            bookmarkNode.CoverImageUrl = request.CoverImageUrl;
            bookmarkNode.Category = request.MediaType.ToString();
            bookmarkNode.Tags = request.Genres.Count > 0 ? string.Join(",", request.Genres) : null;
            bookmarkNode.Status = request.Status;
            bookmarkNode.CurrentProgress = (int)Math.Floor(request.ChaptersRead);

            if (existing is null)
            {
                db.BookmarkNodes.Add(bookmarkNode);
            }
            else
            {
                bookmarkNode.IsDeleted = false;
                bookmarkNode.DeletedAt = null;
                bookmarkNode.PurgeAfter = null;
                bookmarkNode.Version++;
            }

            var trackedSeries = existing ?? new TrackedSeries
            {
                Id = Guid.NewGuid(),
                BookmarkId = bookmarkNode.Id,
                Provider = request.Provider,
                ProviderId = request.ProviderId,
            };

            trackedSeries.MediaType = request.MediaType;
            trackedSeries.LatestKnownChapter = request.LatestChapter;
            trackedSeries.LastChecked = DateTimeOffset.UtcNow;
            trackedSeries.ChaptersRead = request.ChaptersRead;
            trackedSeries.Status = request.Status;

            if (existing is null)
            {
                db.TrackedSeries.Add(trackedSeries);
            }

            object payload = existing is null
                ? new
                {
                    type = "Bookmark",
                    parentBrowserNodeId,
                    title = bookmarkNode.Title,
                    url = bookmarkNode.Url,
                    position = bookmarkNode.Position
                }
                : new
                {
                    bookmarkId = bookmarkNode.Id,
                    type = "Bookmark",
                    parentBrowserNodeId,
                    title = bookmarkNode.Title,
                    url = bookmarkNode.Url,
                    position = bookmarkNode.Position,
                    children = (object?)null
                };

            db.ExtensionCommands.Add(new ExtensionCommandEntry
            {
                Id = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                CommandType = existing is null ? "Create" : "Restore",
                BookmarkId = bookmarkNode.Id,
                BrowserNodeId = null,
                ExpectedVersion = bookmarkNode.Version,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(payload),
                CreatedAt = DateTime.UtcNow,
                Status = Services.DeferredCommandHelper.InitialStatus(parentNode)
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            await BookmarkManager.Api.Infrastructure.SyncWebSocketManager.BroadcastSyncAsync().ConfigureAwait(false);

            return Ok(mapper.Map<BookmarkNodeDto>(bookmarkNode));
        }
        catch (Exception)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    [HttpPost("track/{bookmarkId:guid}/check")]
    public async Task<ActionResult<TrackedSeriesDto>> CheckSeriesRelease(
        Guid bookmarkId,
        CancellationToken cancellationToken)
    {
        var series = await db.TrackedSeries
            .Include(ts => ts.Bookmark)
            .FirstOrDefaultAsync(ts => ts.BookmarkId == bookmarkId && !ts.Bookmark.IsDeleted, cancellationToken)
            .ConfigureAwait(false);

        if (series is null)
        {
            return NotFound("Tracked series not found");
        }

        await watcher.CheckAndUpdateSeriesAsync(db, series, cancellationToken).ConfigureAwait(false);

        return Ok(mapper.Map<TrackedSeriesDto>(series));
    }

    [HttpGet("watcher/status")]
    public async Task<ActionResult<ReleaseWatcherStatusDto>> GetWatcherStatus(CancellationToken cancellationToken)
    {
        var trackedCount = await db.TrackedSeries
            .Include(ts => ts.Bookmark)
            .CountAsync(ts => !ts.Bookmark.IsDeleted, cancellationToken)
            .ConfigureAwait(false);

        return Ok(watcher.GetStatus(trackedCount));
    }

    [HttpPost("watcher/trigger")]
    public IActionResult TriggerWatcher()
    {
        watcher.TriggerCheck();
        return Ok();
    }

    [HttpGet("watcher/settings")]
    public async Task<ActionResult<ReleaseWatcherSettingsDto>> GetWatcherSettings(
        CancellationToken cancellationToken)
    {
        var intervalHours = await db.AppConfig
            .Where(config => config.Id == AppConfigConstants.SingletonId)
            .Select(config => config.ReleaseWatcherIntervalHours)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return Ok(new ReleaseWatcherSettingsDto
        {
            IntervalHours = intervalHours <= 0
                ? AppConfigConstants.DefaultReleaseWatcherIntervalHours
                : intervalHours
        });
    }

    [HttpPut("watcher/settings")]
    public async Task<ActionResult<ReleaseWatcherSettingsDto>> UpdateWatcherSettings(
        [FromBody] ReleaseWatcherSettingsDto settings,
        CancellationToken cancellationToken)
    {
        var config = await db.AppConfig
            .FirstOrDefaultAsync(
                item => item.Id == AppConfigConstants.SingletonId,
                cancellationToken)
            .ConfigureAwait(false);

        if (config is null)
        {
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Application configuration is unavailable.");
        }

        config.ReleaseWatcherIntervalHours = settings.IntervalHours;
        config.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        watcher.NotifyScheduleChanged();

        return Ok(settings);
    }

    [HttpPut("track/{bookmarkId}/progress")]
    public async Task<ActionResult<TrackedSeriesDto>> UpdateProgress(
        Guid bookmarkId,
        [FromBody] UpdateProgressRequest request,
        CancellationToken cancellationToken)
    {
        var ts = await db.TrackedSeries
            .Include(item => item.Bookmark)
            .FirstOrDefaultAsync(
                item => item.BookmarkId == bookmarkId && !item.Bookmark.IsDeleted,
                cancellationToken)
            .ConfigureAwait(false);

        if (ts is null) return NotFound();

        ts.ChaptersRead = request.ChaptersRead;
        ts.Bookmark.CurrentProgress = (int)Math.Floor(request.ChaptersRead);
        ts.Bookmark.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await Infrastructure.SyncWebSocketManager.BroadcastSyncAsync();

        return Ok(mapper.Map<TrackedSeriesDto>(ts));
    }

    [HttpGet("providers/health")]
    public ActionResult<List<ProviderHealthDto>> GetProvidersHealth()
    {
        var dtos = new List<ProviderHealthDto>();
        foreach (var provider in registry.AllProviders)
        {
            var stats = ProviderBudgetTracker.Instance.GetStats(provider.ProviderName);
            dtos.Add(new ProviderHealthDto
            {
                ProviderName = provider.ProviderName,
                IsEnabled = registry.IsProviderEnabled(provider.ProviderName),
                SuccessCount = stats.SuccessCount,
                FailureCount = stats.FailureCount,
                LastSuccess = stats.LastSuccess,
                LastFailure = stats.LastFailure,
                LastError = stats.LastError,
                CacheHits = stats.CacheHits,
                NetworkCalls = stats.NetworkCalls
            });
        }
        return Ok(dtos);
    }

    [HttpPost("providers/{providerName}/toggle")]
    public async Task<IActionResult> ToggleProvider(
        string providerName,
        [FromQuery] bool enabled)
    {
        var provider = registry.FindByName(providerName);
        if (provider is null) return NotFound();

        await registry.SetProviderEnabledAsync(providerName, enabled).ConfigureAwait(false);
        return Ok();
    }
}
