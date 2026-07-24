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
    [EndpointSummary("List notes on a calendar event")]
    [EndpointDescription(
        "Your notes against an upcoming meeting - the \"before\" surface, for jotting an agenda or a question " +
        "to raise while there is still no recording to attach them to.\n\n" +
        "These carry **no capture time**, unlike a recording's notes, because there is no recording clock to " +
        "measure against. They are private to you and scoped to this calendar and event.")]
    public async Task<ActionResult<IReadOnlyList<MeetingNoteDto>>> List(string calendarId, string eventId) =>
        await _db.MeetingNotes
            .Where(n => n.UserId == UserId && n.RecordingId == null
                        && n.CalendarId == calendarId && n.EventId == eventId)
            .OrderBy(n => n.Ordinal)
            .Select(n => new MeetingNoteDto(n.Id, n.Text, n.CapturedAtMs, n.Ordinal, n.CreatedAt))
            .ToListAsync();

    [HttpPost]
    [EndpointSummary("Add notes to a calendar event")]
    [EndpointDescription(
        "Appends one or more note lines to an upcoming meeting. Takes a list so a whole agenda can be saved " +
        "in one call; a single line is a list of one.\n\n" +
        "**When a recording is later linked to this event, these notes are adopted onto it** and appear " +
        "alongside the notes typed during the meeting - so preparation and record end up in one place. Blank " +
        "lines are skipped and long text truncated, so read the response for what was actually created.")]
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
    [EndpointSummary("Edit a calendar event note")]
    [EndpointDescription(
        "Rewrites one note line's text. Empty text is rejected with 400 - delete the note instead; text over " +
        "2048 characters is truncated. Once a recording has adopted these notes, edit them through the " +
        "recording's own notes endpoints rather than here.")]
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
    [EndpointSummary("Delete a calendar event note")]
    [EndpointDescription(
        "Removes one note line from an upcoming meeting permanently. The calendar event itself is untouched - " +
        "nothing here ever writes to your calendar.")]
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
