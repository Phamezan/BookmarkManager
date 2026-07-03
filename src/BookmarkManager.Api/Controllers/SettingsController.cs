using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(AiTaggingSettingsService aiTaggingSettings) : ControllerBase
{
    [HttpGet("ai-tagging")]
    public async Task<ActionResult<AiTaggingSettingsDto>> GetAiTaggingAsync(CancellationToken ct)
        => Ok(await aiTaggingSettings.GetAsync(ct));

    [HttpPut("ai-tagging")]
    public async Task<ActionResult<AiTaggingSettingsDto>> SaveAiTaggingAsync(
        [FromBody] AiTaggingSettingsDto settings,
        CancellationToken ct)
        => Ok(await aiTaggingSettings.SaveAsync(settings, ct));
}
