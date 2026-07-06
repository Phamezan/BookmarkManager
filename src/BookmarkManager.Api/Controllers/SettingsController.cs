using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(
    AiTaggingSettingsService aiTaggingSettings,
    IAiSeriesIdentificationClient aiClient) : ControllerBase
{
    [HttpGet("ai-tagging")]
    public async Task<ActionResult<AiTaggingSettingsDto>> GetAiTaggingAsync(CancellationToken ct)
        => Ok(await aiTaggingSettings.GetAsync(ct));

    [HttpPut("ai-tagging")]
    public async Task<ActionResult<AiTaggingSettingsDto>> SaveAiTaggingAsync(
        [FromBody] AiTaggingSettingsDto settings,
        CancellationToken ct)
        => Ok(await aiTaggingSettings.SaveAsync(settings, ct));

    [HttpPost("ai-tagging/test")]
    public async Task<ActionResult<TestAiKeyResponse>> TestAiTaggingKeyAsync(
        [FromBody] TestAiKeyRequest request,
        CancellationToken ct)
        => Ok(await aiClient.TestConnectionAsync(request, ct));
}
