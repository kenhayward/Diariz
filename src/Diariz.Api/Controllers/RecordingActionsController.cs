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
    private readonly IPromptTemplateProvider _prompts;

    public RecordingActionsController(
        DiarizDbContext db, IActionsClient client, ISummarizationSettingsResolver settings,
        IPromptTemplateProvider prompts)
    {
        _db = db;
        _client = client;
        _settings = settings;
        _prompts = prompts;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Task<bool> OwnsAsync(Guid recordingId) =>
        _db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == UserId);

    private static RecordingActionDto ToDto(RecordingAction a) =>
        new(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal, a.Completed, a.CompletedAt);

    [HttpGet]
    [EndpointSummary("List a recording's action items")]
    [EndpointDescription(
        "The action items for one recording, in display order. Each carries its text, who it is for, a " +
        "free-text deadline, and whether it has been completed. An empty list can mean either that extraction " +
        "found nothing or that it has never been run - `actionsExtracted` on the recording tells them apart. " +
        "Only the owner can see these; anyone else gets 404. For actions across all your recordings, use the " +
        "Actions section instead.")]
    public async Task<ActionResult<IReadOnlyList<RecordingActionDto>>> List(Guid recordingId)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var actions = await _db.RecordingActions
            .Where(a => a.RecordingId == recordingId)
            .OrderBy(a => a.Ordinal)
            .Select(a => new RecordingActionDto(a.Id, a.Text, a.Actor, a.Deadline, a.Ordinal, a.Completed, a.CompletedAt))
            .ToListAsync();
        return actions;
    }

    /// <summary>Run the LLM over the current transcript and replace the recording's action list with the
    /// result (which may be empty). Synchronous: the caller waits for the extracted list.</summary>
    [HttpPost("extract")]
    [EndpointSummary("Extract action items from the transcript")]
    [EndpointDescription(
        "Runs the LLM over the current transcript and returns the action items it found. Unlike most of the " +
        "LLM-backed endpoints this one is **synchronous** - the call blocks until extraction finishes, which " +
        "can take a while on a long meeting, so allow a generous timeout.\n\n" +
        "It **replaces the whole list**, so anything you added or edited by hand is discarded, including " +
        "completion state; a run that finds nothing leaves you with an empty list. Returns 404 when the " +
        "recording has no transcript yet, and 400 when no LLM endpoint is configured for you or the platform.")]
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
                s.StartMs, s.EndMs, s.Original, s.Revised))
            .ToList();

        var template = _prompts.Get("extract-actions", ActionsPrompt.DefaultTemplate);
        var extracted = await _client.ExtractAsync(cfg, segs, template, rec.CreatedAt);

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
    [EndpointSummary("Add an action item")]
    [EndpointDescription(
        "Adds one action item by hand, appended to the end of the list. No LLM is involved, so this works on a " +
        "platform with no model configured. All three fields are free text - the deadline included, so " +
        "\"end of week\" is as valid as a date.\n\n" +
        "Adding by hand also marks the recording as having surfaced actions, so the panel stays visible. Note " +
        "that a later extraction replaces the whole list, including anything added this way.")]
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
    [EndpointSummary("Edit an action item")]
    [EndpointDescription(
        "Updates the text, owner, or deadline. Only the fields you send are changed - omit one (or send null) " +
        "to leave it as it is, and send an empty string to clear it. Completion is **not** set here: mark " +
        "actions done through the Actions section, which handles that across recordings.")]
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
    [EndpointSummary("Delete an action item")]
    [EndpointDescription(
        "Removes one action item permanently. The others keep their order. Deleting every action does not " +
        "reset the recording to \"never extracted\" - the panel stays visible with an empty list.")]
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
