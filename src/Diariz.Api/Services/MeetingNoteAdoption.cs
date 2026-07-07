using Diariz.Domain;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>Moves a user's event-anchored note lines onto a recording when its calendar link forms (the
/// LinkCalendar chokepoint - both the auto-match save and manual linking). One-way and additive: adopted
/// lines append after any lines already on the recording; unlinking never detaches notes; linking to a
/// different event later adopts that event's notes too. Caller SaveChanges.</summary>
public static class MeetingNoteAdoption
{
    /// <summary>Returns how many lines were adopted. Does not save.</summary>
    public static async Task<int> AdoptAsync(
        DiarizDbContext db, Guid userId, Guid recordingId, string calendarId, string eventId, CancellationToken ct)
    {
        var pending = await db.MeetingNotes
            .Where(n => n.UserId == userId && n.RecordingId == null
                        && n.CalendarId == calendarId && n.EventId == eventId)
            .OrderBy(n => n.Ordinal)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var next = (await db.MeetingNotes
            .Where(n => n.RecordingId == recordingId)
            .Select(n => (int?)n.Ordinal)
            .MaxAsync(ct) ?? -1) + 1;

        foreach (var note in pending)
        {
            note.RecordingId = recordingId;
            note.CalendarId = null;
            note.EventId = null;
            note.Ordinal = next++;
        }
        return pending.Count;
    }
}
