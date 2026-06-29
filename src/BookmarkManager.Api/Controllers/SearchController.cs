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
            var q = request.Query;
            query = query.Where(n =>
                EF.Functions.Like(n.Title, $"%{q}%") ||
                (n.Url != null && EF.Functions.Like(n.Url, $"%{q}%")) ||
                (n.Tags != null && EF.Functions.Like(n.Tags, $"%{q}%")));
        }

        if (!string.IsNullOrWhiteSpace(request.Category))
            query = query.Where(n => n.Category == request.Category);

        if (request.IsFavorite.HasValue)
            query = query.Where(n => n.IsFavorite == request.IsFavorite.Value);

        var total = await query.CountAsync(ct);

        query = query.OrderByDescending(n => n.UpdatedAt);

        var items = await query
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(ct);

        return new PagedResult<BookmarkNodeDto>
        {
            Items = _mapper.Map<List<BookmarkNodeDto>>(items),
            TotalCount = total,
            Page = request.Page,
            PageSize = request.PageSize
        };
    }
}
