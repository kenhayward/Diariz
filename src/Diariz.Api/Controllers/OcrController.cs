using Diariz.Api.Services.Llm;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Controllers;

/// <summary>Whether this platform can read text off a capture.
///
/// <para>Its own endpoint rather than a field bolted onto the chat-model list, because it answers a
/// different question about a different call group, and the viewer needs it before it can decide whether
/// to draw the extract buttons at all. Hiding an action nobody has configured is better than offering one
/// that always fails - most deployments will never route an OCR model.</para>
///
/// <para>Authenticated but not administrator-only: it discloses nothing but the model's public name, which
/// every user already sees in the provenance line on any text it produces.</para></summary>
[ApiController]
[Authorize]
[Route("api/ocr")]
public class OcrController(ILlmSettingsResolver settings) : ControllerBase
{
    [HttpGet("status")]
    [EndpointSummary("Whether screenshot OCR is available")]
    [EndpointDescription(
        "Reports whether a Platform Administrator has routed a model to the OCR call type, and which model " +
        "it is. When `enabled` is false the screenshot OCR endpoint returns 400, so a client should hide or " +
        "disable the action rather than offering it.")]
    public async Task<ActionResult<OcrStatusDto>> Status(CancellationToken ct)
    {
        var cfg = await settings.ResolveAsync(LlmCallKind.ScreenshotOcr, ct);
        return new OcrStatusDto(cfg.Enabled, cfg.Enabled ? cfg.Model : null);
    }
}

/// <summary>Whether OCR is routed, and the model that would run. <paramref name="Model"/> is null exactly
/// when <paramref name="Enabled"/> is false.</summary>
public record OcrStatusDto(bool Enabled, string? Model);
