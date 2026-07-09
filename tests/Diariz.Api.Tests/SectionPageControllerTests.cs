using Diariz.Api.Contracts;
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
        new(db, queue ?? new FakeJobQueue(), resolver ?? new FakeSummarizationSettingsResolver(), new FakeHubContext())
        { ControllerContext = Http.Context(userId) };

    private static async Task<Section> Section(DiarizDbContext db, Guid userId, Guid? parentId = null)
    {
        var s = new Diariz.Domain.Entities.Section { Id = Guid.NewGuid(), UserId = userId, Name = "F", ParentId = parentId };
        db.Sections.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    private static async Task<Recording> Recording(
        DiarizDbContext db, Guid userId, Guid? sectionId, string? name = null, string title = "Rec",
        long durationMs = 1000, DateTimeOffset? createdAt = null)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, SectionId = sectionId, Name = name, Title = title,
            DurationMs = durationMs, BlobKey = "k", CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
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
        Assert.IsType<NotFoundResult>((await Build(db, Guid.NewGuid()).Get(section.Id)).Result);
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
        var othersType = new MeetingType { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Title = "Theirs" };
        db.MeetingTypes.Add(othersType);
        await db.SaveChangesAsync();
        var queue = new FakeJobQueue();

        // Another user's Personal type → NotFound, nothing enqueued.
        Assert.IsType<NotFoundResult>(
            await Build(db, userId, queue).GenerateMinutes(section.Id, new ApplyMeetingTypeRequest(othersType.Id)));
        Assert.Empty(queue.SectionMinutesEnqueued);

        // Own valid type (null = General) → enqueues + persists the chosen id.
        var mine = new MeetingType { Id = Guid.NewGuid(), UserId = userId, Title = "Mine" };
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
}
