using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services.Rag;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/library")]
public sealed class LibraryRagController(ILibraryRagService ragService) : ControllerBase
{
    /// <summary>Answers a natural-language question grounded on the local catalog and returns markdown
    /// plus the series cards the answer drew from.</summary>
    [HttpPost("chat")]
    public async Task<ActionResult<LibraryChatResponseDto>> Chat(
        [FromBody] LibraryChatRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Message))
            return BadRequest();

        var response = await ragService.ChatAsync(request, cancellationToken).ConfigureAwait(false);
        return Ok(response);
    }
}
