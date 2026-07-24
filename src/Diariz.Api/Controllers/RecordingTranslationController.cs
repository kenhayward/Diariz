using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Localization;
using Diariz.Api.Services;
using Diariz.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Translate a transcript (all segments + the summary + actions) — or a single segment — into a
/// target language using the configured LLM. Translations land in each segment's <c>Revised</c> column
/// (the model's <c>Original</c> is preserved), mirroring a manual edit. Synchronous, like action
/// extraction. The target defaults to the caller's saved native language.</summary>
[ApiController]
[Authorize]
[Route("api/recordings/{recordingId:guid}")]
public class RecordingTranslationController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly ITranslationClient _client;
    private readonly ISummarizationSettingsResolver _settings;

    public RecordingTranslationController(
        DiarizDbContext db, ITranslationClient client, ISummarizationSettingsResolver settings)
    {
        _db = db;
        _client = client;
        _settings = settings;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Resolve the target language: the request value, else the caller's native language. Returns
    /// the language's English name (for the prompt), or an error result.</summary>
    private async Task<(string? name, ActionResult? error)> ResolveTargetAsync(string? requested)
    {
        var code = requested;
        if (string.IsNullOrWhiteSpace(code))
            code = (await _db.UserSettings.FindAsync(UserId))?.NativeLanguage;
        if (string.IsNullOrWhiteSpace(code))
            return (null, BadRequest("Choose a language to translate into (or set your native language in Preferences)."));

        var lang = SupportedLanguages.Find(code);
        return lang is null ? (null, BadRequest("Unknown language.")) : (lang.EnglishName, null);
    }

    [HttpPost("translate")]
    [EndpointSummary("Translate the whole recording")]
    [EndpointDescription(
        "Translates every segment, the summary, and the action items into one language. Omit `language` to use " +
        "your saved native language. **Synchronous** - the call blocks until the whole transcript has been " +
        "translated, so allow a generous timeout on a long meeting.\n\n" +
        "Segments are translated into the same revision column that manual edits use, so the model's original " +
        "text is preserved and you can flip back - but **any correction you had typed on a segment is " +
        "overwritten**. The summary and the action items are translated **in place**, replacing the original " +
        "wording (regenerate or re-extract to get it back). Action owners are left alone, being names.\n\n" +
        "Returns 404 when the recording has no transcript, and 400 for an unknown language, when no language " +
        "is given and you have no native language set, or when no LLM endpoint is configured.")]
    public async Task<IActionResult> TranslateRecording(Guid recordingId, TranslateRequest req)
    {
        var rec = await _db.Recordings
            .Include(r => r.Actions)
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Segments.OrderBy(s => s.Ordinal))
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Summary)
            .FirstOrDefaultAsync(r => r.Id == recordingId && r.UserId == UserId);
        if (rec is null) return NotFound();

        var current = rec.Transcriptions.FirstOrDefault();
        if (current is null || current.Segments.Count == 0) return NotFound();

        var cfg = await _settings.ResolveAsync(UserId);
        if (!cfg.Enabled) return BadRequest("Translation needs an LLM endpoint. Set one in Settings.");

        var (name, error) = await ResolveTargetAsync(req.Language);
        if (error is not null) return error;

        // Segments: translate each model Original into the revision column.
        var segments = current.Segments.OrderBy(s => s.Ordinal).ToList();
        var translated = await _client.TranslateAsync(cfg, name!, segments.Select(s => s.Original).ToList());
        for (var i = 0; i < segments.Count; i++) segments[i].Revised = translated[i];

        // Summary (in place — it is regenerable by re-summarising).
        if (current.Summary is { Text.Length: > 0 } summary)
            summary.Text = (await _client.TranslateAsync(cfg, name!, [summary.Text]))[0];

        // Actions (in place): translate the task text + deadline; keep the actor (a name) untouched.
        var actions = rec.Actions.OrderBy(a => a.Ordinal).ToList();
        if (actions.Count > 0)
        {
            var texts = actions.SelectMany(a => new[] { a.Text, a.Deadline }).ToList();
            var tr = await _client.TranslateAsync(cfg, name!, texts);
            for (var i = 0; i < actions.Count; i++)
            {
                actions[i].Text = tr[2 * i];
                actions[i].Deadline = tr[2 * i + 1];
            }
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("segments/{segmentId:guid}/translate")]
    [EndpointSummary("Translate one segment")]
    [EndpointDescription(
        "Translates a single line, for checking one exchange without touching the rest of the transcript. The " +
        "translation lands in the segment's revision column, so the original is kept but any correction you " +
        "had typed on that line is overwritten. The summary and actions are untouched. Omit `language` to use " +
        "your saved native language.")]
    public async Task<IActionResult> TranslateSegment(Guid recordingId, Guid segmentId, TranslateRequest req)
    {
        var seg = await _db.Segments.Include(s => s.Transcription)
            .FirstOrDefaultAsync(s => s.Id == segmentId);
        if (seg?.Transcription is null || seg.Transcription.RecordingId != recordingId) return NotFound();
        if (!await _db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == UserId)) return NotFound();

        var cfg = await _settings.ResolveAsync(UserId);
        if (!cfg.Enabled) return BadRequest("Translation needs an LLM endpoint. Set one in Settings.");

        var (name, error) = await ResolveTargetAsync(req.Language);
        if (error is not null) return error;

        seg.Revised = (await _client.TranslateAsync(cfg, name!, [seg.Original]))[0];
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Translate a set of selected segments (the Select-mode bulk translate) into the target language
    /// in one batched call — each segment's <c>Original</c> → <c>Revised</c>. Ids not on the caller's recording
    /// are ignored; summary/actions are untouched.</summary>
    [HttpPost("segments/translate")]
    [EndpointSummary("Translate several segments")]
    [EndpointDescription(
        "Translates a chosen set of lines in one batched LLM call, which is markedly cheaper and faster than " +
        "translating each on its own. Ids that are not on this recording's current transcript are skipped, and " +
        "an empty or entirely unmatched list succeeds without doing anything. Same behaviour as the " +
        "single-segment version otherwise: revisions are overwritten, the summary and actions are untouched.")]
    public async Task<IActionResult> TranslateSegments(Guid recordingId, TranslateSegmentsRequest req)
    {
        var current = await _db.Transcriptions.Where(t => t.RecordingId == recordingId)
            .OrderByDescending(t => t.Version).FirstOrDefaultAsync();
        if (current is null) return NotFound();
        if (!await _db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == UserId)) return NotFound();

        var cfg = await _settings.ResolveAsync(UserId);
        if (!cfg.Enabled) return BadRequest("Translation needs an LLM endpoint. Set one in Settings.");

        var (name, error) = await ResolveTargetAsync(req.Language);
        if (error is not null) return error;

        var ids = (req.Ids ?? Array.Empty<Guid>()).ToHashSet();
        var segments = await _db.Segments
            .Where(s => s.TranscriptionId == current.Id && ids.Contains(s.Id))
            .OrderBy(s => s.Ordinal)
            .ToListAsync();
        if (segments.Count == 0) return NoContent();

        var translated = await _client.TranslateAsync(cfg, name!, segments.Select(s => s.Original).ToList());
        for (var i = 0; i < segments.Count; i++) segments[i].Revised = translated[i];
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
