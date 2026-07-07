using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

public class MeetingNoteAdoptionTests
{
    [Fact]
    public async Task Adopt_MovesEventNotesOntoRecording_AppendingAfterExistingLines_AndClearsEventKeys()
    {
        var db = TestDb.Create();
        var user = Guid.NewGuid();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user, Title = "R" };
        db.Recordings.Add(rec);
        db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = user, RecordingId = rec.Id, Text = "live line", CapturedAtMs = 1000, Ordinal = 0 });
        db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = user, CalendarId = "cal1", EventId = "evt1", Text = "prep 1", Ordinal = 0 });
        db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = user, CalendarId = "cal1", EventId = "evt1", Text = "prep 2", Ordinal = 1 });
        await db.SaveChangesAsync();

        var adopted = await MeetingNoteAdoption.AdoptAsync(db, user, rec.Id, "cal1", "evt1", default);
        await db.SaveChangesAsync();

        Assert.Equal(2, adopted);
        var lines = await db.MeetingNotes.Where(n => n.RecordingId == rec.Id).OrderBy(n => n.Ordinal).ToListAsync();
        Assert.Equal(["live line", "prep 1", "prep 2"], lines.Select(n => n.Text));
        Assert.Equal([0, 1, 2], lines.Select(n => n.Ordinal));
        Assert.All(lines, n => { Assert.Null(n.CalendarId); Assert.Null(n.EventId); });
    }

    [Fact]
    public async Task Adopt_TouchesOnlyTheOwnersNotes_ForThatEvent()
    {
        var db = TestDb.Create();
        var user = Guid.NewGuid();
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user, Title = "R" };
        db.Recordings.Add(rec);
        db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), CalendarId = "cal1", EventId = "evt1", Text = "someone else's", Ordinal = 0 });
        db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = user, CalendarId = "cal1", EventId = "other", Text = "other event", Ordinal = 0 });
        await db.SaveChangesAsync();

        Assert.Equal(0, await MeetingNoteAdoption.AdoptAsync(db, user, rec.Id, "cal1", "evt1", default));
        Assert.Equal(2, await db.MeetingNotes.CountAsync(n => n.RecordingId == null)); // both untouched
    }
}
