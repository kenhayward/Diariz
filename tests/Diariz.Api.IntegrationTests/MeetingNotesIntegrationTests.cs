using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres checks for meeting notes: CRUD round-trip + ordering, adoption at the calendar
/// link, and the two cascades (recording delete, user delete for event-anchored lines).</summary>
[Collection(IntegrationCollection.Name)]
public class MeetingNotesIntegrationTests(ContainersFixture fx)
{
    private static MeetingNotesController Controller(Diariz.Domain.DiarizDbContext db, Guid userId) =>
        new(db) { ControllerContext = Http.Context(userId) };

    private async Task<(Guid userId, Guid recId)> SeedUserAndRecording()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = $"{Guid.NewGuid()}@x.test" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, Title = "R", BlobKey = "k" };
        db.Users.Add(user);
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return (user.Id, rec.Id);
    }

    [Fact]
    public async Task Notes_RoundTrip_OrderedByOrdinal()
    {
        var (user, rec) = await SeedUserAndRecording();

        await using (var db = fx.CreateDbContext())
            await Controller(db, user).Create(rec, new CreateMeetingNotesRequest([new("first", 1_000), new("second", null)]));

        await using (var db = fx.CreateDbContext())
        {
            var list = (await Controller(db, user).List(rec)).Value!;
            Assert.Equal(["first", "second"], list.Select(n => n.Text));
            Assert.Equal(1_000, list[0].CapturedAtMs);
            Assert.Equal([0, 1], list.Select(n => n.Ordinal));
        }
    }

    [Fact]
    public async Task Adoption_MergesEventNotes_OverRealPostgres()
    {
        var (user, rec) = await SeedUserAndRecording();

        await using (var db = fx.CreateDbContext())
        {
            db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = user, RecordingId = rec, Text = "live", CapturedAtMs = 500, Ordinal = 0 });
            db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = user, CalendarId = "cal", EventId = "evt", Text = "prep", Ordinal = 0 });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            Assert.Equal(1, await MeetingNoteAdoption.AdoptAsync(db, user, rec, "cal", "evt", default));
            await db.SaveChangesAsync();
        }

        await using (var verify = fx.CreateDbContext())
        {
            var lines = await verify.MeetingNotes.Where(n => n.RecordingId == rec).OrderBy(n => n.Ordinal).ToListAsync();
            Assert.Equal(["live", "prep"], lines.Select(n => n.Text));
            Assert.All(lines, n => Assert.Null(n.EventId));
        }
    }

    [Fact]
    public async Task DeletingRecording_CascadesItsNotes()
    {
        var (user, rec) = await SeedUserAndRecording();
        await using (var db = fx.CreateDbContext())
            await Controller(db, user).Create(rec, new CreateMeetingNotesRequest([new("x")]));

        await using (var db = fx.CreateDbContext())
        {
            db.Recordings.Remove((await db.Recordings.FindAsync(rec))!);
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.False(await verify.MeetingNotes.AnyAsync(n => n.RecordingId == rec));
    }

    [Fact]
    public async Task DeletingUser_CascadesEventAnchoredNotes()
    {
        var (user, _) = await SeedUserAndRecording();
        await using (var db = fx.CreateDbContext())
        {
            db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = user, CalendarId = "cal", EventId = "evt", Text = "prep", Ordinal = 0 });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Users.Remove((await db.Users.FindAsync(user))!);
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.False(await verify.MeetingNotes.AnyAsync(n => n.UserId == user));
    }
}
