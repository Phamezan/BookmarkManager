using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    LibraryProviderRegistry registry,
    LibraryCatalogSyncBackgroundService catalogSync,
    BookmarkSeriesMatchService matchService,
    AppDbContext db) : ControllerBase
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

    [HttpGet("catalog/enrich")]
    public async Task<ActionResult<LibraryEntryDto>> EnrichCatalogEntry(
        [FromQuery] string provider,
        [FromQuery] string providerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerId))
            return BadRequest();

        var entry = await searchService.EnrichEntryAsync(provider, providerId, cancellationToken).ConfigureAwait(false);
        return entry is null ? NotFound() : Ok(entry);
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

    [HttpGet("reading-progress")]
    public async Task<ActionResult<List<LibraryReadingProgressDto>>> GetReadingProgress(CancellationToken cancellationToken)
    {
        var matches = await matchService.GetMatchesAsync(cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
            return Ok(new List<LibraryReadingProgressDto>());

        var providers = matches.Select(m => m.Provider).Distinct().ToList();
        var providerIds = matches.Select(m => m.ProviderId).Distinct().ToList();
        var candidateEntries = await db.LibraryCatalogEntries
            .Where(e => providers.Contains(e.Provider) && providerIds.Contains(e.ProviderId))
            .Select(e => new { e.Provider, e.ProviderId, e.LatestChapter })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var latestChapterByKey = candidateEntries.ToDictionary(e => (e.Provider, e.ProviderId), e => e.LatestChapter);

        var bookmarkIds = matches.Select(m => m.BookmarkId).Distinct().ToList();
        var bookmarksById = await db.BookmarkNodes
            .Where(b => bookmarkIds.Contains(b.Id))
            .Select(b => new { b.Id, b.Title, b.Url })
            .ToDictionaryAsync(b => b.Id, b => b, cancellationToken)
            .ConfigureAwait(false);

        var dtos = matches.Select(match =>
        {
            latestChapterByKey.TryGetValue((match.Provider, match.ProviderId), out var latestChapter);
            bookmarksById.TryGetValue(match.BookmarkId, out var bookmark);
            return new LibraryReadingProgressDto(
                match.Provider,
                match.ProviderId,
                match.CurrentChapter,
                match.RawProgressText,
                LatestChapterParser.Parse(latestChapter),
                match.BookmarkId,
                bookmark?.Title,
                bookmark?.Url);
        }).ToList();

        return Ok(dtos);
    }

    /// <summary>Full catalog cards for the user's matched bookmarks - local-DB only, independent
    /// of whatever page of trending/search results the client currently has loaded. Backs the
    /// "My bookmarks" filter, which needs cards for series that may not be in the top
    /// trending/search page at all.</summary>
    [HttpGet("my-bookmarks")]
    public async Task<ActionResult<List<LibraryEntryDto>>> GetMyBookmarkedSeries(CancellationToken cancellationToken)
    {
        var matches = await matchService.GetMatchesAsync(cancellationToken).ConfigureAwait(false);
        var keys = matches.Select(m => (m.Provider, m.ProviderId)).ToList();
        var entries = await searchService.GetEntriesByKeysAsync(keys, cancellationToken).ConfigureAwait(false);
        return Ok(entries);
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
