using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public partial class BookmarksController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IMapper _mapper;
    private readonly BookmarkManager.Api.Services.TagExtractorService _tagExtractor;
    private readonly BookmarkManager.Api.Services.BookmarkTaggingService _bookmarkTagging;
    private readonly BookmarkManager.Api.Services.Library.BookmarkSeriesMatchService _matchService;

    public BookmarksController(
        AppDbContext db,
        IMapper mapper,
        BookmarkManager.Api.Services.TagExtractorService tagExtractor,
        BookmarkManager.Api.Services.BookmarkTaggingService bookmarkTagging,
        BookmarkManager.Api.Services.Library.BookmarkSeriesMatchService matchService)
    {
        _db = db;
        _mapper = mapper;
        _tagExtractor = tagExtractor;
        _bookmarkTagging = bookmarkTagging;
        _matchService = matchService;
    }
}
