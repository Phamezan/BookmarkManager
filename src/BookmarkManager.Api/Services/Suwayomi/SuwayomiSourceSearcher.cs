using BookmarkManager.Contracts;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>
/// Searches the configured Suwayomi sources for a title: one query per source in
/// <see cref="SuwayomiOptions.SourceOrder"/> (or the single requested source), capped at
/// <see cref="SuwayomiOptions.MaxResultsPerSource"/> hits each. Shared by the match dialog and Explore.
/// </summary>
public sealed class SuwayomiSourceSearcher
{
    private readonly ISuwayomiClient _client;
    private readonly SuwayomiOptions _options;

    public SuwayomiSourceSearcher(ISuwayomiClient client, IOptions<SuwayomiOptions> options)
    {
        _client = client;
        _options = options.Value;
    }

    /// <summary>
    /// Hits in source order. Empty when Suwayomi is unreachable; a source that fails is skipped.
    /// <paramref name="source"/> restricts the search to one named source.
    /// </summary>
    public async Task<List<SuwayomiSearchResultDto>> SearchAsync(string query, string? source, CancellationToken ct)
    {
        SuwayomiServerStatus server;
        try
        {
            server = await _client.GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [];
        }

        var english = server.Sources
            .Where(s => string.Equals(s.Lang, "en", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var all = server.Sources;

        string? requested = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        var names = requested is null ? _options.SourceOrder : [requested];

        var results = new List<SuwayomiSearchResultDto>();
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var match = english.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                continue;
            }

            IReadOnlyList<SuwayomiManga> hits;
            try
            {
                hits = await _client.SearchAsync(match.Id, query, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            results.AddRange(hits.Take(_options.MaxResultsPerSource).Select(h => new SuwayomiSearchResultDto
            {
                MangaId = h.Id,
                Title = h.Title,
                SourceName = match.Name,
                InLibrary = h.InLibrary
            }));
        }

        return results;
    }
}
