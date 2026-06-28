using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Action items for a recording: extract them from the transcript with the configured LLM, then
/// list/add/edit/remove them. Actions are user-editable free text and shown "by exception" — only once an
/// extraction has run (tracked by <see cref="Recording.ActionsExtractedAt"/>).</summary>
[ApiController]
[Authorize]
[Route("api/recordings/{recordingId:guid}/actions")]
public class RecordingActionsController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IActionsClient _client;
    private readonly ISummarizationSettingsResolver _settings;

    public RecordingActionsController(
        DiarizDbContext db, IActionsClient client, ISummarizationSettingsResolver settings)
    {
        _db = db;
        _client = client;
        _settings = settings;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Task<bool> OwnsAsync(Guid recordingId) =>
        _db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == UserId);

    private static RecordingActionDto ToDto(RecordingAction a) =>
        new(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RecordingActionDto>>> List(Guid recordingId)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var actions = await _db.RecordingActions
            .Where(a => a.RecordingId == recordingId)
            .OrderBy(a => a.Ordinal)
            .Select(a => new RecordingActionDto(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal))
            .ToListAsync();
        return actions;
    }

    /// <summary>Run the LLM over the current transcript and replace the recording's action list with the
    /// result (which may be empty). Synchronous: the caller waits for the extracted list.</summary>
    [HttpPost("extract")]
    public async Task<ActionResult<IReadOnlyList<RecordingActionDto>>> Extract(Guid recordingId)
    {
        var rec = await _db.Recordings
            .Include(r => r.Speakers)
            .Include(r => r.Actions)
            .Include(r => r.Transcriptions.OrderByDescending(t => t.Version).Take(1))
                .ThenInclude(t => t.Segments.OrderBy(s => s.Ordinal))
            .FirstOrDefaultAsync(r => r.Id == recordingId && r.UserId == UserId);
        if (rec is null) return NotFound();

        var current = rec.Transcriptions.FirstOrDefault();
        if (current is null || current.Segments.Count == 0) return NotFound();

        var cfg = await _settings.ResolveAsync(UserId);
        if (!cfg.Enabled)
            return BadRequest("Action extraction needs an LLM endpoint. Set one in Settings.");

        var names = rec.Speakers.ToDictionary(s => s.Label, s => s.DisplayName);
        var segs = current.Segments
            .OrderBy(s => s.Ordinal)
            .Select(s => new SegmentDto(
                s.Id, s.SpeakerLabel,
                names.TryGetValue(s.SpeakerLabel, out var dn) ? dn : s.SpeakerLabel,
                s.StartMs, s.EndMs, s.Text))
            .ToList();

        var extracted = await _client.ExtractAsync(cfg, segs);

        // Replace the whole list with the fresh extraction.
        _db.RecordingActions.RemoveRange(rec.Actions);
        var ordinal = 0;
        var fresh = extracted.Select(e => new RecordingAction
        {
            Id = Guid.NewGuid(),
            RecordingId = recordingId,
            Text = e.Text,
            Actor = e.Actor,
            Deadline = e.Deadline,
            Ordinal = ordinal++,
        }).ToList();
        _db.RecordingActions.AddRange(fresh);
        rec.ActionsExtractedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        return fresh.Select(ToDto).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<RecordingActionDto>> Create(Guid recordingId, CreateRecordingActionRequest req)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();

        var maxOrdinal = await _db.RecordingActions
            .Where(a => a.RecordingId == recordingId)
            .Select(a => (int?)a.Ordinal)
            .MaxAsync();

        var action = new RecordingAction
        {
            Id = Guid.NewGuid(),
            RecordingId = recordingId,
            Text = req.Text?.Trim() ?? "",
            Actor = req.Actor?.Trim() ?? "",
            Deadline = req.Deadline?.Trim() ?? "",
            Ordinal = (maxOrdinal ?? -1) + 1,
        };
        _db.RecordingActions.Add(action);

        // A manual add still marks the recording as "actions surfaced" so the panel persists.
        var rec = await _db.Recordings.FirstAsync(r => r.Id == recordingId);
        rec.ActionsExtractedAt ??= DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync();
        return ToDto(action);
    }

    [HttpPut("{actionId:guid}")]
    public async Task<IActionResult> Update(Guid recordingId, Guid actionId, UpdateRecordingActionRequest req)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();

        var action = await _db.RecordingActions
            .FirstOrDefaultAsync(a => a.Id == actionId && a.RecordingId == recordingId);
        if (action is null) return NotFound();

        if (req.Text is not null) action.Text = req.Text.Trim();
        if (req.Actor is not null) action.Actor = req.Actor.Trim();
        if (req.Deadline is not null) action.Deadline = req.Deadline.Trim();
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{actionId:guid}")]
    public async Task<IActionResult> Delete(Guid recordingId, Guid actionId)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();

        var action = await _db.RecordingActions
            .FirstOrDefaultAsync(a => a.Id == actionId && a.RecordingId == recordingId);
        if (action is null) return NotFound();

        _db.RecordingActions.Remove(action);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
