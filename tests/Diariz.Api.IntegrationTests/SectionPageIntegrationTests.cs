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
}
