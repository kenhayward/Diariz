using System.Security.Claims;
using Diariz.Api.Contracts;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Controllers;

/// <summary>Pre-meeting note lines anchored to a calendar event (see <see cref="MeetingNote"/>) - the
/// "before" surface. Scoped to the signed-in user + event pair; there is no recording yet, so ownership is
/// the user themselves. When a recording links to the event, these lines are adopted onto it
/// (<c>MeetingNoteAdoption</c>). Event notes never carry a recording-clock timestamp.</summary>
[ApiController]
[Authorize]
[Route("api/calendar/events/{calendarId}/{eventId}/notes")]
public class CalendarEventNotesController : ControllerBase
{
    private readonly DiarizDbContext _db;
    public CalendarEventNotesController(DiarizDbContext db) => _db = db;

    private Guid UserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static MeetingNoteDto ToDto(MeetingNote n) => new(n.Id, n.Text, n.CapturedAtMs, n.Ordinal, n.CreatedAt);

    private static string Clamp(string value) => value.Length > 256 ? value[..256] : value;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MeetingNoteDto>>> List(string calendarId, string eventId) =>
        await _db.MeetingNotes
            .Where(n => n.UserId == UserId && n.RecordingId == null
                        && n.CalendarId == calendarId && n.EventId == eventId)
            .OrderBy(n => n.Ordinal)
            .Select(n => new MeetingNoteDto(n.Id, n.Text, n.CapturedAtMs, n.Ordinal, n.CreatedAt))
            .ToListAsync();

    [HttpPost]
    public async Task<ActionResult<IReadOnlyList<MeetingNoteDto>>> Create(
        string calendarId, string eventId, CreateMeetingNotesRequest req)
    {
        var next = (await _db.MeetingNotes
            .Where(n => n.UserId == UserId && n.RecordingId == null
                        && n.CalendarId == calendarId && n.EventId == eventId)
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
                CalendarId = Clamp(calendarId),
                EventId = Clamp(eventId),
                Text = text,
                CapturedAtMs = null, // event notes never carry a recording-clock stamp
                Ordinal = next++,
            });
        }
        _db.MeetingNotes.AddRange(fresh);
        await _db.SaveChangesAsync();
        return fresh.Select(ToDto).ToList();
    }

    [HttpPut("{noteId:guid}")]
    public async Task<IActionResult> Update(string calendarId, string eventId, Guid noteId, UpdateMeetingNoteRequest req)
    {
        var note = await _db.MeetingNotes.FirstOrDefaultAsync(n =>
            n.Id == noteId && n.UserId == UserId && n.RecordingId == null
            && n.CalendarId == calendarId && n.EventId == eventId);
        if (note is null) return NotFound();

        var text = (req.Text ?? "").Trim();
        if (text.Length == 0) return BadRequest("Note text is required.");
        note.Text = text.Length > 2048 ? text[..2048] : text;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{noteId:guid}")]
    public async Task<IActionResult> Delete(string calendarId, string eventId, Guid noteId)
    {
        var note = await _db.MeetingNotes.FirstOrDefaultAsync(n =>
            n.Id == noteId && n.UserId == UserId && n.RecordingId == null
            && n.CalendarId == calendarId && n.EventId == eventId);
        if (note is null) return NotFound();
        _db.MeetingNotes.Remove(note);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
