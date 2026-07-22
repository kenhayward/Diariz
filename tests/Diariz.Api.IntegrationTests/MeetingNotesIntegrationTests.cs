using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres checks for meeting notes: CRUD round-trip + ordering, adoption at the calendar
/// link, and the two cascades (recording delete, user delete for event-anchored lines).</summary>
[Collection(IntegrationCollection.Name)]
public class MeetingNotesIntegrationTests(ContainersFixture fx)
{
    private static MeetingNotesController Controller(Diariz.Domain.DiarizDbContext db, Guid userId) =>
        new(db, new RoomScope(db)) { ControllerContext = Http.Context(userId) };

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

    /// <summary>The permission boundary: the owner can read and mutate; a room co-viewer can read (List) but
    /// is rejected on every mutating route; a stranger with no room in common is rejected on everything -
    /// all against real Postgres.</summary>
    [Fact]
    public async Task PermissionBoundary_OwnerCoViewerAndStranger_OnRealPostgres()
    {
        var (owner, rec) = await SeedUserAndRecording();
        var viewerId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();

        Guid noteId;
        await using (var db = fx.CreateDbContext())
        {
            var created = (await Controller(db, owner).Create(rec, new CreateMeetingNotesRequest([new("x")]))).Value!;
            noteId = created[0].Id;

            db.Users.Add(new ApplicationUser { Id = viewerId, UserName = $"{viewerId}@x.test", Email = $"{viewerId}@x.test" });
            db.Users.Add(new ApplicationUser { Id = strangerId, UserName = $"{strangerId}@x.test", Email = $"{strangerId}@x.test" });
            await db.SaveChangesAsync();

            var rooms = new RoomScope(db);
            var roomId = await rooms.CreateSharedRoomAsync($"Engineering {Guid.NewGuid():N}", null, null, null);
            await rooms.SetMemberAsync(roomId, RoomPrincipalType.User, viewerId, RoomPermission.CreateRecording);
            await rooms.ShareIntoRoomAsync(rec, roomId, owner, sectionId: null);
        }

        // Owner: reads and mutates freely.
        await using (var db = fx.CreateDbContext())
        {
            Assert.Single((await Controller(db, owner).List(rec)).Value!);
            Assert.IsType<NoContentResult>(await Controller(db, owner).Update(rec, noteId, new UpdateMeetingNoteRequest("y")));
        }

        // Co-viewer: List succeeds, every mutating route 404s.
        await using (var db = fx.CreateDbContext())
        {
            var viewer = Controller(db, viewerId);
            Assert.Single((await viewer.List(rec)).Value!);
            Assert.IsType<NotFoundResult>((await viewer.Create(rec, new CreateMeetingNotesRequest([new("z")]))).Result);
            Assert.IsType<NotFoundResult>(await viewer.Update(rec, noteId, new UpdateMeetingNoteRequest("z")));
            Assert.IsType<NotFoundResult>(await viewer.Delete(rec, noteId));
        }

        // Stranger: rejected on everything.
        await using (var db = fx.CreateDbContext())
        {
            var stranger = Controller(db, strangerId);
            Assert.IsType<NotFoundResult>((await stranger.List(rec)).Result);
            Assert.IsType<NotFoundResult>((await stranger.Create(rec, new CreateMeetingNotesRequest([new("z")]))).Result);
            Assert.IsType<NotFoundResult>(await stranger.Update(rec, noteId, new UpdateMeetingNoteRequest("z")));
            Assert.IsType<NotFoundResult>(await stranger.Delete(rec, noteId));
        }

        // The note survived the co-viewer/stranger's rejected mutation attempts, still "y" from the owner's edit.
        await using var verify = fx.CreateDbContext();
        Assert.Equal("y", (await verify.MeetingNotes.FindAsync(noteId))!.Text);
    }
}
