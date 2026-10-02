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
        => Ok(AiTaggingSettingsMasking.ToMasked(await aiTaggingSettings.GetAsync(ct)));

    [HttpPut("ai-tagging")]
    public async Task<ActionResult<AiTaggingSettingsDto>> SaveAiTaggingAsync(
        [FromBody] AiTaggingSettingsDto settings,
        CancellationToken ct)
    {
        // Secrets arrive masked/empty; merge against what is stored so an untouched field keeps
        // its key and only a newly typed value (or an explicit Clear* flag) changes it.
        var stored = await aiTaggingSettings.GetAsync(ct);

        // A preserved key must not be silently re-pointed at a new endpoint (that would send the
        // stored secret to an attacker-controlled host later). Require the key to be re-entered.
        var endpointViolation = AiTaggingSettingsMasking.FindEndpointChangeViolation(settings, stored);
        if (endpointViolation is not null)
            return Problem(
                statusCode: 400,
                title: "Endpoint change requires key re-entry",
                detail: endpointViolation);

        // A mask-shaped value that is not this field's own mask (e.g. another secret's mask pasted
        // in) must never be stored as if it were a real key.
        var maskViolation = AiTaggingSettingsMasking.FindMaskInjectionViolation(settings, stored);
        if (maskViolation is not null)
            return Problem(
                statusCode: 400,
                title: "Invalid masked key value",
                detail: maskViolation);

        var merged = AiTaggingSettingsMasking.MergeSecrets(settings, stored);
        var saved = await aiTaggingSettings.SaveAsync(merged, ct);
        return Ok(AiTaggingSettingsMasking.ToMasked(saved));
    }

    [HttpPost("ai-tagging/test")]
    public async Task<ActionResult<TestAiKeyResponse>> TestAiTaggingKeyAsync(
        [FromBody] TestAiKeyRequest request,
        CancellationToken ct)
    {
        // Never require the browser to hold the real key: fall back to the stored secret when the
        // request is empty or still carries the masked value returned by GET.
        var stored = await aiTaggingSettings.GetAsync(ct);
        var effective = AiTaggingSettingsMasking.MergeTestSecret(request, stored);
        return Ok(await aiClient.TestConnectionAsync(effective, ct));
    }
}
