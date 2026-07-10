using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;

    public SearchController(AppDbContext db, IMapper mapper)
    {
        _db = db;
        _mapper = mapper;
    }

    [HttpPost]
    public async Task<PagedResult<BookmarkNodeDto>> SearchAsync(
        [FromBody] SearchRequest request,
        CancellationToken ct)
    {
        var query = _db.BookmarkNodes
            .Where(n => !n.IsDeleted && n.Type == NodeType.Bookmark)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var q = EscapeLike(request.Query.Trim());
            query = query.Where(n =>
                EF.Functions.Like(n.Title, $"%{q}%", "\\") ||
                (n.Url != null && EF.Functions.Like(n.Url, $"%{q}%", "\\")) ||
                (n.Tags != null && EF.Functions.Like(n.Tags, $"%{q}%", "\\")));
        }

        if (!string.IsNullOrWhiteSpace(request.Category))
            query = query.Where(n => n.Category == request.Category);

        if (request.IsFavorite.HasValue)
            query = query.Where(n => n.IsFavorite == request.IsFavorite.Value);

        // Tag filter: tags are stored as a comma-separated string. To get
        // boundary-correct matching ("Dev" must not match "Development") we
        // match against ",tag," on a comma-padded projection. SQLite translates
        // this to LIKE with the usual % wildcards.
        if (request.Tags is { Count: > 0 })
        {
            foreach (var tag in request.Tags)
            {
                var t = tag.Trim();
                if (t.Length == 0) continue;
                var escapedTag = EscapeLike(t);
                var needle = $"%,{escapedTag},%";
                query = query.Where(n =>
                    n.Tags != null
                    && EF.Functions.Like("," + n.Tags + ",", needle, "\\"));
            }
        }

        var total = await query.CountAsync(ct);

        var pageSize = Math.Max(1, Math.Min(request.PageSize, 100));
        var page = Math.Max(1, request.Page);

        query = query.OrderByDescending(n => n.UpdatedAt);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var dtos = _mapper.Map<List<BookmarkNodeDto>>(items);
        await PopulateTrackingInfoAsync(dtos, ct);

        return new PagedResult<BookmarkNodeDto>
        {
            Items = dtos,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        };
    }

    private async Task PopulateTrackingInfoAsync(List<BookmarkNodeDto> dtos, CancellationToken ct)
    {
        var bookmarkIds = dtos.Where(d => d.Type == NodeType.Bookmark).Select(d => d.Id).ToList();
        if (bookmarkIds.Count == 0) return;

        var tracked = await _db.TrackedSeries
            .Where(t => bookmarkIds.Contains(t.BookmarkId))
            .Select(t => new { t.BookmarkId, t.LatestKnownChapter, t.ChaptersRead, t.LatestChapterUrl })
            .ToListAsync(ct);

        var dict = tracked.ToDictionary(t => t.BookmarkId, t => t);

        foreach (var dto in dtos)
        {
            if (dict.TryGetValue(dto.Id, out var ts))
            {
                dto.IsTracked = true;
                dto.ChaptersRead = ts.ChaptersRead;
                dto.LatestKnownChapter = ts.LatestKnownChapter;
                dto.LatestChapterUrl = ts.LatestChapterUrl;
                dto.ChaptersBehind = TrackedSeries.CalculateChaptersBehind(
                    ts.LatestKnownChapter,
                    ts.ChaptersRead);
            }
        }
    }

    private static string EscapeLike(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return input
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }
}
