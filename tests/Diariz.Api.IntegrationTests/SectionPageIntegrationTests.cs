using Diariz.Api.Services;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres fidelity for the folder page: the new cascade/SetNull relationships and the
/// section-scoped aggregation joins (which must translate under Npgsql, not just the in-memory provider).</summary>
[Collection(IntegrationCollection.Name)]
public class SectionPageIntegrationTests(ContainersFixture fx)
{
    private static Task<Guid> RoomOf(Diariz.Domain.DiarizDbContext db, Guid owner) => new RoomScope(db).PersonalRoomIdAsync(owner);

    private SectionPageController Build(Diariz.Domain.DiarizDbContext db, Guid userId) =>
        new(db, new FakeJobQueue(), new FakeSummarizationSettingsResolver(), new FakeHubContext(), new RoomScope(db))
        { ControllerContext = Http.Context(userId) };

    /// <summary>Real Postgres enforces the Section/Recording → AspNetUsers FK, so tests seed a real user.</summary>
    private async Task<Guid> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Deleting_a_section_cascades_to_its_summary_and_minutes()
    {
        var userId = await SeedUser();
        var sectionId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.Sections.Add(new Section { Id = sectionId, UserId = userId, RoomId = await RoomOf(db, userId), Name = "F" });
            db.SectionSummaries.Add(new SectionSummary { Id = Guid.NewGuid(), SectionId = sectionId, Text = "s" });
            db.SectionMinutes.Add(new SectionMinutes { Id = Guid.NewGuid(), SectionId = sectionId, Text = "m" });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Sections.Remove(await db.Sections.SingleAsync(s => s.Id == sectionId));
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.False(await verify.SectionSummaries.AnyAsync(x => x.SectionId == sectionId));
        Assert.False(await verify.SectionMinutes.AnyAsync(x => x.SectionId == sectionId));
    }

    [Fact]
    public async Task Deleting_a_meeting_type_setnulls_the_folder_minutes_reference()
    {
        var userId = await SeedUser();
        var sectionId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.MeetingTypes.Add(new MeetingType { Id = typeId, UserId = userId, Title = "T" });
            db.Sections.Add(new Section { Id = sectionId, UserId = userId, RoomId = await RoomOf(db, userId), Name = "F" });
            db.SectionMinutes.Add(new SectionMinutes { Id = Guid.NewGuid(), SectionId = sectionId, MeetingTypeId = typeId });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.MeetingTypes.Remove(await db.MeetingTypes.SingleAsync(t => t.Id == typeId));
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        var minutes = await verify.SectionMinutes.SingleAsync(x => x.SectionId == sectionId);
        Assert.Null(minutes.MeetingTypeId); // SetNull kept the folder minutes, dropped the template reference
    }

    [Fact]
    public async Task Deleting_a_parent_section_cascades_to_child_folder_artifacts()
    {
        var userId = await SeedUser();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.Sections.Add(new Section { Id = parentId, UserId = userId, RoomId = await RoomOf(db, userId), Name = "Parent" });
            db.Sections.Add(new Section { Id = childId, UserId = userId, RoomId = await RoomOf(db, userId), Name = "Child", ParentId = parentId });
            db.SectionSummaries.Add(new SectionSummary { Id = Guid.NewGuid(), SectionId = childId, Text = "s" });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Sections.Remove(await db.Sections.SingleAsync(s => s.Id == parentId));
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.False(await verify.Sections.AnyAsync(s => s.Id == childId));
        Assert.False(await verify.SectionSummaries.AnyAsync(x => x.SectionId == childId));
    }

    [Fact]
    public async Task Actions_aggregation_join_translates_under_postgres()
    {
        var userId = await SeedUser();
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.Sections.Add(new Section { Id = parentId, UserId = userId, RoomId = await RoomOf(db, userId), Name = "P" });
            db.Sections.Add(new Section { Id = childId, UserId = userId, RoomId = await RoomOf(db, userId), Name = "C", ParentId = parentId });
            db.Recordings.Add(new Recording { Id = recId, UserId = userId, Title = "R", BlobKey = "k" });
            db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = recId, Text = "Do it", Ordinal = 0 });
            var ungroupedId = Guid.NewGuid();
            db.Recordings.Add(new Recording { Id = ungroupedId, UserId = userId, Title = "U", BlobKey = "k" });
            await db.SaveChangesAsync();
            // The folder lives on the placement now: file R under the child folder, U ungrouped. The ungrouped
            // (null SectionId) placement must not trip the null-safe filter.
            await new RoomScope(db).PlaceInMainRoomAsync(recId, userId, childId);
            await new RoomScope(db).PlaceInMainRoomAsync(ungroupedId, userId, sectionId: null);
        }

        await using var db2 = fx.CreateDbContext();
        var items = (await Build(db2, userId).Actions(parentId)).Value!;
        Assert.Equal("Do it", Assert.Single(items).Text);
        Assert.Equal("R", items[0].RecordingName);
    }

    /// <summary>The Notes aggregation join (MeetingNotes + Recordings + RoomRecordings) must translate under
    /// real Postgres, and its projected <c>RecordedByUserId</c> must be the RECORDING's owner - what
    /// <c>MeetingNotesController.OwnsAsync</c> actually gates mutation on - not the note's own denormalized
    /// <c>UserId</c>. Seeds a note whose own UserId deliberately differs from the recording owner to pin that.</summary>
    [Fact]
    public async Task Notes_aggregation_returns_the_recordings_owner_under_postgres()
    {
        var ownerId = await SeedUser();
        var noteAuthorId = await SeedUser(); // a different user id than the recording owner
        var sectionId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.Sections.Add(new Section { Id = sectionId, UserId = ownerId, RoomId = await RoomOf(db, ownerId), Name = "F" });
            db.Recordings.Add(new Recording { Id = recId, UserId = ownerId, Title = "R", BlobKey = "k" });
            db.MeetingNotes.Add(new MeetingNote
            {
                Id = Guid.NewGuid(), UserId = noteAuthorId, RecordingId = recId, Text = "note", Ordinal = 0,
            });
            await db.SaveChangesAsync();
            await new RoomScope(db).PlaceInMainRoomAsync(recId, ownerId, sectionId);
        }

        await using var db2 = fx.CreateDbContext();
        var notes = (await Build(db2, ownerId).Notes(sectionId)).Value!;
        Assert.Equal("note", Assert.Single(notes).Text);
        Assert.Equal(ownerId, notes[0].RecordedByUserId); // the recording's owner, not noteAuthorId
    }

    /// <summary>Review finding: the unit-test coverage for a genuinely multi-owner folder (two recordings from
    /// TWO DIFFERENT owners, both filed in the SAME section) is a faithful stand-in for this flat, un-grouped
    /// join - but pins it against real Postgres too, matching the paranoia that already produced
    /// <see cref="Notes_aggregation_returns_the_recordings_owner_under_postgres"/> for the single-owner case.
    /// Two recordings owned by two different users are shared into the same section of a Shared room; each of
    /// the three aggregations must attribute its two rows to their own distinct recording owner in one response.</summary>
    [Fact]
    public async Task Aggregations_attribute_each_row_to_its_own_owner_under_postgres()
    {
        var ownerA = await SeedUser();
        var ownerB = await SeedUser();
        var sectionId = Guid.NewGuid();
        var recAId = Guid.NewGuid();
        var recBId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            var rooms = new RoomScope(db);
            // Unique room name: the integration suite runs sequentially against one set of containers, and a
            // fixed name like "Engineering" has collided with other tests' seed data before.
            var roomId = await rooms.CreateSharedRoomAsync($"Two-Owner Folder {Guid.NewGuid():N}", null, null, null);
            await rooms.SetMemberAsync(roomId, RoomPrincipalType.User, ownerA, RoomPermission.CreateRecording);
            await rooms.SetMemberAsync(roomId, RoomPrincipalType.User, ownerB, RoomPermission.CreateRecording);
            db.Sections.Add(new Section { Id = sectionId, UserId = ownerA, RoomId = roomId, Name = "F" });
            db.Recordings.Add(new Recording { Id = recAId, UserId = ownerA, Title = "Rec A", BlobKey = "k" });
            db.Recordings.Add(new Recording { Id = recBId, UserId = ownerB, Title = "Rec B", BlobKey = "k" });
            db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = recAId, Text = "A action", Ordinal = 0 });
            db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = recBId, Text = "B action", Ordinal = 0 });
            db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = ownerA, RecordingId = recAId, Text = "A note", Ordinal = 0 });
            db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = ownerB, RecordingId = recBId, Text = "B note", Ordinal = 0 });
            db.Attachments.Add(new Attachment { Id = Guid.NewGuid(), RecordingId = recAId, Kind = AttachmentKind.File, Name = "a.pdf", SizeBytes = 1, Ordinal = 0 });
            db.Attachments.Add(new Attachment { Id = Guid.NewGuid(), RecordingId = recBId, Kind = AttachmentKind.File, Name = "b.pdf", SizeBytes = 1, Ordinal = 0 });
            await db.SaveChangesAsync();
            // Both recordings land in the SAME shared section, shared in by their own respective owner.
            await rooms.ShareIntoRoomAsync(recAId, roomId, ownerA, sectionId);
            await rooms.ShareIntoRoomAsync(recBId, roomId, ownerB, sectionId);
        }

        await using var db2 = fx.CreateDbContext();
        var actions = (await Build(db2, ownerA).Actions(sectionId)).Value!;
        var notes = (await Build(db2, ownerA).Notes(sectionId)).Value!;
        var attachments = (await Build(db2, ownerA).Attachments(sectionId)).Value!;

        Assert.Equal(2, actions.Count);
        Assert.Equal(ownerA, Assert.Single(actions, i => i.Text == "A action").RecordedByUserId);
        Assert.Equal(ownerB, Assert.Single(actions, i => i.Text == "B action").RecordedByUserId);

        Assert.Equal(2, notes.Count);
        Assert.Equal(ownerA, Assert.Single(notes, i => i.Text == "A note").RecordedByUserId);
        Assert.Equal(ownerB, Assert.Single(notes, i => i.Text == "B note").RecordedByUserId);

        Assert.Equal(2, attachments.Count);
        Assert.Equal(ownerA, Assert.Single(attachments, i => i.Name == "a.pdf").RecordedByUserId);
        Assert.Equal(ownerB, Assert.Single(attachments, i => i.Name == "b.pdf").RecordedByUserId);
    }
}
