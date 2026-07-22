using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>The user's own note lines for a recording (see <see cref="MeetingNote"/>). Text is editable;
/// capture timestamps are immutable facts. Blank lines are skipped on create; text is trimmed.
///
/// Read (list) is open to anyone who can read the recording - the owner, or a member of a room it is placed
/// in (see <see cref="IRoomScope.CanReadRecordingAsync"/>), so a room co-viewer sees the same notes woven
/// into the transcript as the owner does. Create/update/delete stay strictly owner-only.</summary>
[ApiController]
[Authorize]
[Route("api/recordings/{recordingId:guid}/notes")]
public class MeetingNotesController : ControllerBase
{
    private readonly DiarizDbContext _db;
    private readonly IRoomScope _rooms;
    public MeetingNotesController(DiarizDbContext db, IRoomScope rooms)
    {
        _db = db;
        _rooms = rooms;
    }

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Owner-only gate for the mutating routes (create/update/delete).</summary>
    private Task<bool> OwnsAsync(Guid recordingId) =>
        _db.Recordings.AnyAsync(r => r.Id == recordingId && r.UserId == UserId);

    /// <summary>Read gate for list: the owner, or a member of a room the recording is placed in.</summary>
    private Task<bool> CanReadAsync(Guid recordingId) => _rooms.CanReadRecordingAsync(UserId, recordingId);

    private static MeetingNoteDto ToDto(MeetingNote n) => new(n.Id, n.Text, n.CapturedAtMs, n.Ordinal, n.CreatedAt);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MeetingNoteDto>>> List(Guid recordingId)
    {
        if (!await CanReadAsync(recordingId)) return NotFound();
        return await _db.MeetingNotes
            .Where(n => n.RecordingId == recordingId)
            .OrderBy(n => n.Ordinal)
            .Select(n => new MeetingNoteDto(n.Id, n.Text, n.CapturedAtMs, n.Ordinal, n.CreatedAt))
            .ToListAsync();
    }

    /// <summary>Bulk append (the live panel attaches all its lines after upload; single adds send one line).
    /// Blank lines are skipped, text trimmed, ordinals continue after existing lines.</summary>
    [HttpPost]
    public async Task<ActionResult<IReadOnlyList<MeetingNoteDto>>> Create(Guid recordingId, CreateMeetingNotesRequest req)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();

        var next = (await _db.MeetingNotes
            .Where(n => n.RecordingId == recordingId)
            .Select(n => (int?)n.Ordinal)
            .MaxAsync() ?? -1) + 1;

        var fresh = new List<MeetingNote>();
        foreach (var line in req.Lines)
        {
            var text = (line.Text ?? "").Trim();
            if (text.Length == 0) continue;
            if (text.Length > 2048) text = text[..2048];
            fresh.Add(new MeetingNote
            {
                Id = Guid.NewGuid(),
                UserId = UserId,
                RecordingId = recordingId,
                Text = text,
                CapturedAtMs = line.CapturedAtMs,
                Ordinal = next++,
            });
        }
        _db.MeetingNotes.AddRange(fresh);
        await _db.SaveChangesAsync();
        return fresh.Select(ToDto).ToList();
    }

    [HttpPut("{noteId:guid}")]
    public async Task<IActionResult> Update(Guid recordingId, Guid noteId, UpdateMeetingNoteRequest req)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var note = await _db.MeetingNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.RecordingId == recordingId);
        if (note is null) return NotFound();

        var text = (req.Text ?? "").Trim();
        if (text.Length == 0) return BadRequest("Note text is required.");
        note.Text = text.Length > 2048 ? text[..2048] : text;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{noteId:guid}")]
    public async Task<IActionResult> Delete(Guid recordingId, Guid noteId)
    {
        if (!await OwnsAsync(recordingId)) return NotFound();
        var note = await _db.MeetingNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.RecordingId == recordingId);
        if (note is null) return NotFound();
        _db.MeetingNotes.Remove(note);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
