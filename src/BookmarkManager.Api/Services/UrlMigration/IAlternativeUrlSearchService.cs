using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BookmarkManager.Api.Services.UrlMigration;

// NOTE: SeriesExtraction is defined in ISeriesExtractionService.cs (owned by the extraction
// service, plan section 6.1/6.2).
public interface IAlternativeUrlSearchService
{
    Task<IReadOnlyList<SearchCandidate>> SearchAsync(SeriesExtraction extraction, string deadHost, CancellationToken ct);
}

public sealed record SearchCandidate(string Url, string? Title, string? Snippet);
