using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>The folder (section) page endpoints: aggregated stats + read aggregations of actions/notes/
/// attachments across the section and its children, and the async generate + edit of the folder summary/minutes.</summary>
public class SectionPageControllerTests
{
    private static SectionPageController Build(
        DiarizDbContext db, Guid userId, FakeJobQueue? queue = null, FakeSummarizationSettingsResolver? resolver = null) =>
        new(db, queue ?? new FakeJobQueue(), resolver ?? new FakeSummarizationSettingsResolver(), new FakeHubContext(),
            new RoomScope(db))
        { ControllerContext = Http.Context(userId) };

    private static async Task<Section> Section(DiarizDbContext db, Guid userId, Guid? parentId = null)
    {
        if (await db.Users.FindAsync(userId) is null)
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
            await db.SaveChangesAsync();
        }
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(userId); // folders are room-scoped now
        var s = new Diariz.Domain.Entities.Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "F", ParentId = parentId };
        db.Sections.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    // Seeds a recording and its main placement in the owner's personal room, filed under sectionId (null =
    // ungrouped). The folder lives on the placement now, not on Recording.SectionId.
    private static async Task<Recording> Recording(
        DiarizDbContext db, Guid userId, Guid? sectionId, string? name = null, string title = "Rec",
        long durationMs = 1000, DateTimeOffset? createdAt = null)
    {
        if (await db.Users.FindAsync(userId) is null)
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
            await db.SaveChangesAsync();
        }
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Name = name, Title = title,
            DurationMs = durationMs, BlobKey = "k", CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, userId, sectionId);
        return rec;
    }

    [Fact]
    public async Task Get_aggregates_stats_across_section_and_children()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await Section(db, userId);
        var child = await Section(db, userId, parent.Id);
        await Recording(db, userId, parent.Id, durationMs: 1000, createdAt: DateTimeOffset.UtcNow.AddDays(-2));
        await Recording(db, userId, child.Id, durationMs: 2000, createdAt: DateTimeOffset.UtcNow.AddDays(-1));
        await Recording(db, userId, null); // ungrouped — excluded

        var dto = (await Build(db, userId).Get(parent.Id)).Value!;

        Assert.Equal(2, dto.Stats.TranscriptCount);
        Assert.Equal(3000, dto.Stats.TotalDurationMs);
        Assert.NotNull(dto.Stats.FirstRecordingAt);
        Assert.True(dto.Stats.FirstRecordingAt < dto.Stats.LastRecordingAt);
    }

    [Fact]
    public async Task Get_empty_folder_has_zeroed_stats_and_null_dates()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);

        var dto = (await Build(db, userId).Get(section.Id)).Value!;

        Assert.Equal(0, dto.Stats.TranscriptCount);
        Assert.Equal(0, dto.Stats.TotalDurationMs);
        Assert.Null(dto.Stats.FirstRecordingAt);
        Assert.Null(dto.Stats.LastRecordingAt);
    }

    [Fact]
    public async Task Get_other_users_section_is_not_found()
    {
        using var db = TestDb.Create();
        var section = await Section(db, Guid.NewGuid());
        var caller = Guid.NewGuid();
        Users.Ensure(db, caller); // the caller's personal room is minted when scoping the query
        Assert.IsType<NotFoundResult>((await Build(db, caller).Get(section.Id)).Result);
    }

    [Fact]
    public async Task Actions_aggregate_across_children_with_meeting_name_and_exclude_others()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await Section(db, userId);
        var child = await Section(db, userId, parent.Id);
        var recA = await Recording(db, userId, parent.Id, name: "Kickoff");
        var recB = await Recording(db, userId, child.Id, title: "Untitled Review");
        db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = recA.Id, Text = "Do A", Ordinal = 0 });
        db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = recB.Id, Text = "Do B", Ordinal = 0 });
        // Another user's action under a like-named section must not leak in.
        var other = await Recording(db, Guid.NewGuid(), (await Section(db, Guid.NewGuid())).Id);
        db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = other.Id, Text = "Secret", Ordinal = 0 });
        await db.SaveChangesAsync();

        var items = (await Build(db, userId).Actions(parent.Id)).Value!;

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i is { Text: "Do A", RecordingName: "Kickoff" });
        Assert.Contains(items, i => i is { Text: "Do B", RecordingName: "Untitled Review" }); // Name ?? Title
        Assert.DoesNotContain(items, i => i.Text == "Secret");
    }

    [Fact]
    public async Task Notes_and_attachments_aggregate_with_meeting_name()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        var rec = await Recording(db, userId, section.Id, name: "Sync");
        db.MeetingNotes.Add(new MeetingNote { Id = Guid.NewGuid(), UserId = userId, RecordingId = rec.Id, Text = "note", Ordinal = 0 });
        db.Attachments.Add(new Attachment { Id = Guid.NewGuid(), RecordingId = rec.Id, Kind = AttachmentKind.File, Name = "spec.pdf", SizeBytes = 10, Ordinal = 0 });
        await db.SaveChangesAsync();

        var notes = (await Build(db, userId).Notes(section.Id)).Value!;
        var atts = (await Build(db, userId).Attachments(section.Id)).Value!;

        Assert.Equal("note", Assert.Single(notes).Text);
        Assert.Equal("Sync", notes[0].RecordingName);
        Assert.Equal("spec.pdf", Assert.Single(atts).Name);
        Assert.Equal("Sync", atts[0].RecordingName);
    }

    [Fact]
    public async Task GenerateSummary_enqueues_sets_generating_and_is_idempotent()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        var queue = new FakeJobQueue();

        var first = await Build(db, userId, queue).GenerateSummary(section.Id);

        Assert.IsType<AcceptedResult>(first);
        Assert.Single(queue.SectionSummaryEnqueued);
        Assert.Equal(SectionGenerationStatus.Generating,
            (await db.SectionSummaries.SingleAsync(x => x.SectionId == section.Id)).Status);

        // Second call while Generating: no second enqueue.
        await Build(db, userId, queue).GenerateSummary(section.Id);
        Assert.Single(queue.SectionSummaryEnqueued);
    }

    [Fact]
    public async Task GenerateSummary_without_llm_configured_is_bad_request()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        var resolver = new FakeSummarizationSettingsResolver { Config = new("", "", "m", 60) };

        var result = await Build(db, userId, resolver: resolver).GenerateSummary(section.Id);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GenerateMinutes_validates_type_ownership_and_persists_choice()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);
        var othersType = new MeetingType { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), RoomId = Guid.NewGuid(), Title = "Theirs" };
        db.MeetingTypes.Add(othersType);
        await db.SaveChangesAsync();
        var queue = new FakeJobQueue();

        // Another user's Personal type → NotFound, nothing enqueued.
        Assert.IsType<NotFoundResult>(
            await Build(db, userId, queue).GenerateMinutes(section.Id, new ApplyMeetingTypeRequest(othersType.Id)));
        Assert.Empty(queue.SectionMinutesEnqueued);

        // Own valid type (null = General) → enqueues + persists the chosen id.
        var mine = new MeetingType { Id = Guid.NewGuid(), UserId = userId, RoomId = await new RoomScope(db).PersonalRoomIdAsync(userId), Title = "Mine" };
        db.MeetingTypes.Add(mine);
        await db.SaveChangesAsync();

        Assert.IsType<AcceptedResult>(
            await Build(db, userId, queue).GenerateMinutes(section.Id, new ApplyMeetingTypeRequest(mine.Id)));
        Assert.Single(queue.SectionMinutesEnqueued);
        Assert.Equal(mine.Id, (await db.SectionMinutes.SingleAsync(x => x.SectionId == section.Id)).MeetingTypeId);
    }

    [Fact]
    public async Task UpdateSummary_and_UpdateMinutes_mark_user_edited()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await Section(db, userId);

        Assert.IsType<NoContentResult>(await Build(db, userId).UpdateSummary(section.Id, new UpdateSummaryRequest("hand summary")));
        Assert.IsType<NoContentResult>(await Build(db, userId).UpdateMinutes(section.Id, new UpdateMeetingMinutesRequest("hand minutes")));

        var s = await db.SectionSummaries.SingleAsync(x => x.SectionId == section.Id);
        Assert.True(s.IsUserEdited);
        Assert.Equal("hand summary", s.Text);
        Assert.Equal(SectionSummary.UserEditedModel, s.Model);
        var m = await db.SectionMinutes.SingleAsync(x => x.SectionId == section.Id);
        Assert.True(m.IsUserEdited);
        Assert.Equal("hand minutes", m.Text);
    }

    // Creates a folder inside a shared room the given user belongs to (with the given permission).
    private static async Task<Diariz.Domain.Entities.Section> SharedRoomSection(
        DiarizDbContext db, Guid memberId, RoomPermission perm = RoomPermission.ManageContents)
    {
        Users.Ensure(db, memberId);
        var scope = new RoomScope(db);
        var roomId = await scope.CreateSharedRoomAsync("Eng", null, null, null);
        await scope.SetMemberAsync(roomId, RoomPrincipalType.User, memberId, perm);
        var s = new Diariz.Domain.Entities.Section { Id = Guid.NewGuid(), UserId = memberId, RoomId = roomId, Name = "Shared F" };
        db.Sections.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    [Fact]
    public async Task Get_returns_a_folder_in_a_shared_room_the_caller_belongs_to()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var section = await SharedRoomSection(db, me);

        // Regression (issue #289): a folder in a shared room used to 404 (the controller hardcoded the
        // caller's personal room), leaving the page stuck on "Loading ...".
        var dto = (await Build(db, me).Get(section.Id)).Value!;

        Assert.Equal(section.Id, dto.Id);
        Assert.Equal("Shared F", dto.Name);
    }

    [Fact]
    public async Task Get_a_folder_in_a_room_the_caller_is_not_in_is_not_found()
    {
        using var db = TestDb.Create();
        var owner = Guid.NewGuid();
        var section = await SharedRoomSection(db, owner);
        var outsider = Guid.NewGuid();
        Users.Ensure(db, outsider);

        Assert.IsType<NotFoundResult>((await Build(db, outsider).Get(section.Id)).Result);
    }

    [Fact]
    public async Task Aggregations_read_a_shared_room_folder_for_a_member()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var section = await SharedRoomSection(db, me);

        // A member gets an (empty) list, not a 404 (Value non-null means it wasn't NotFound).
        Assert.Empty((await Build(db, me).Actions(section.Id)).Value!);
        Assert.Empty((await Build(db, me).Notes(section.Id)).Value!);
        Assert.Empty((await Build(db, me).Attachments(section.Id)).Value!);
    }

    [Fact]
    public async Task GenerateSummary_in_a_shared_room_needs_ManageContents()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        // A member who can view but not manage contents cannot generate a folder summary.
        var section = await SharedRoomSection(db, me, RoomPermission.CreateRecording);

        var result = await Build(db, me).GenerateSummary(section.Id);

        Assert.IsType<ForbidResult>(result);
    }
}
