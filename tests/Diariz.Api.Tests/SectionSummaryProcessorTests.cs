using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>Orchestration of a folder-summary job: gather the section's + child sections' recordings,
/// regenerate only the ones missing an individual summary, combine into one folder summary on the section.</summary>
public class SectionSummaryProcessorTests
{
    /// <summary>A folder in the user's real personal room - the same room <see cref="SeedRecording"/> files
    /// recordings into. The <c>RoomId</c> is load-bearing: placements are resolved by the folder's own room, so
    /// a section left at the default <c>Guid.Empty</c> would match no placement at all.</summary>
    private static async Task<Section> SeedSection(DiarizDbContext db, Guid userId, Guid? parentId = null)
    {
        if (await db.Users.FindAsync(userId) is null)
        {
            db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
            await db.SaveChangesAsync();
        }
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(userId);
        var s = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "Folder", ParentId = parentId };
        db.Sections.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    /// <summary>A recording with a current transcription + one segment, optionally pre-loaded with a summary.
    /// Filed under sectionId via its main placement in the owner's personal room (the folder lives there now).</summary>
    private static async Task<Recording> SeedRecording(
        DiarizDbContext db, Guid userId, Guid? sectionId, string? name = null, string? summaryText = null,
        bool withSegments = true)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Name = name, BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        if (withSegments)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 1000, Original = "Hi", Ordinal = 0
            });
        if (summaryText is not null)
            db.Summaries.Add(new Summary { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "m", Text = summaryText });
        await db.SaveChangesAsync();
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, userId, sectionId);
        return rec;
    }

    private static Task Run(DiarizDbContext db, ISummarizationClient perRec, IMeetingMinutesClient combiner,
        FakeSummarizationSettingsResolver resolver, FakeHubContext hub, Section section) =>
        SectionSummaryProcessor.ProcessAsync(db, perRec, combiner, resolver, hub,
            SummarizationPrompt.DefaultTemplate, FolderSummaryPrompt.DefaultTemplate,
            new SectionSummaryJob(section.Id), NullLogger.Instance);

    /// <summary>A large folder must roll up EVERY meeting, not the first ~18 that fit an arbitrary constant.
    /// The reduce step drops whole items once its char budget is spent (<see cref="FolderSummaryPrompt.JoinItems"/>),
    /// and that budget used to be a hard-coded 24,000 chars with no relationship to the model's context window -
    /// so a folder of 30 meetings quietly lost a third of them, with only an in-prompt note to say so. The budget
    /// now comes from the resolved config, sized off the configured window.</summary>
    [Fact]
    public async Task Large_folder_rolls_up_every_meeting_without_omitting_any()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId);

        // 30 meetings x ~1,500-char summaries = ~45,000 chars, comfortably past the old 24,000 cap.
        const int meetings = 30;
        for (var i = 0; i < meetings; i++)
            await SeedRecording(db, userId, section.Id, name: $"Meeting {i:00}",
                summaryText: $"Summary {i:00}. " + new string('x', 1_500));

        var combiner = new FakeMeetingMinutesClient();
        await Run(db, new FakeSummarizationClient(), combiner,
            new FakeSummarizationSettingsResolver(), new FakeHubContext(), section);

        var prompt = combiner.LastMessages![1].Content;
        for (var i = 0; i < meetings; i++)
            Assert.Contains($"Summary {i:00}.", prompt);
        Assert.DoesNotContain("omitted to fit the length limit", prompt);
    }

    /// <summary>Three-level folder chain (Customers > Acme > Falcon) in the user's real personal room, with a
    /// summarisable recording filed two levels down under Falcon. Proves <c>IncludedRecordingsAsync</c> walks
    /// the whole subtree, not just direct children - the bug fixed by scoping the folder walk through
    /// <see cref="SectionTree.SubtreeIdsAsync"/> by <c>RoomId</c> instead of a one-level <c>UserId</c>/<c>ParentId</c>
    /// query. Unlike <see cref="SeedSection"/>, these sections get a real <c>RoomId</c> (the user's personal
    /// room, matching what <see cref="RoomScope.PlaceInMainRoomAsync"/> places the recording into) so the
    /// room-scoped walk has something meaningful to match against.</summary>
    [Fact]
    public async Task Reaches_a_recording_filed_two_levels_below_the_folder()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
        await db.SaveChangesAsync();
        var roomId = await new RoomScope(db).PersonalRoomIdAsync(userId);

        var customers = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "Customers" };
        var acme = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "Acme", ParentId = customers.Id };
        var falcon = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "Falcon", ParentId = acme.Id };
        db.Sections.AddRange(customers, acme, falcon);
        await db.SaveChangesAsync();

        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Name = "Kickoff", BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 1000, Original = "Hi", Ordinal = 0,
        });
        await db.SaveChangesAsync();
        await new RoomScope(db).PlaceInMainRoomAsync(rec.Id, userId, falcon.Id); // two levels below Customers

        var perRec = new FakeSummarizationClient { Result = new SummaryResult("Grandchild summary.", null) };
        var combiner = new FakeMeetingMinutesClient();

        await Run(db, perRec, combiner, new FakeSummarizationSettingsResolver(), new FakeHubContext(), customers);

        // The grandchild's recording was reached and summarized - before the fix this is 0 (only the direct
        // child "Acme" was visible to the old UserId/ParentId query, so Falcon's recording was invisible).
        Assert.Equal(1, perRec.Calls);
        Assert.Contains("Grandchild summary.", combiner.LastMessages![1].Content);
    }

    /// <summary>A folder in a SHARED room rolls up the recordings placed in that room. Before the fix this
    /// returned nothing at all, whatever the folder held: the folder walk produced section ids from the shared
    /// room while the placement join only ever looked in the section owner's PERSONAL room, so the two sets were
    /// drawn from different rooms and could never intersect. The folder page meanwhile showed a non-zero count
    /// for the same folder, because it scopes placements by <c>p.RoomId == roomId</c>.</summary>
    [Fact]
    public async Task Reaches_recordings_in_a_shared_room_folder()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, UserName = $"{userId}@x.test", Email = $"{userId}@x.test" });
        await db.SaveChangesAsync();
        var scope = new RoomScope(db);
        var sharedRoomId = await scope.CreateSharedRoomAsync("Engineering", null, null, null);

        var folder = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = sharedRoomId, Name = "Acme" };
        db.Sections.Add(folder);
        await db.SaveChangesAsync();

        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Name = "Kickoff", BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 1000, Original = "Hi", Ordinal = 0,
        });
        // Placed in the SHARED room, filed under the shared folder - which is where a shared folder's
        // recordings actually live.
        db.RoomRecordings.Add(new RoomRecording { RoomId = sharedRoomId, RecordingId = rec.Id, SectionId = folder.Id });
        await db.SaveChangesAsync();

        var perRec = new FakeSummarizationClient { Result = new SummaryResult("Shared room summary.", null) };
        var combiner = new FakeMeetingMinutesClient();

        await Run(db, perRec, combiner, new FakeSummarizationSettingsResolver(), new FakeHubContext(), folder);

        Assert.Equal(1, perRec.Calls);
        Assert.Contains("Shared room summary.", combiner.LastMessages![1].Content);
    }

    [Fact]
    public async Task Combines_summaries_across_section_and_children_and_notifies()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId);
        var child = await SeedSection(db, userId, parent.Id);
        await SeedRecording(db, userId, parent.Id, name: "A", summaryText: "Alpha summary.");
        await SeedRecording(db, userId, child.Id, name: "B", summaryText: "Beta summary.");
        var combiner = new FakeMeetingMinutesClient { Result = "Folder-level summary." };
        var hub = new FakeHubContext();

        await Run(db, new FakeSummarizationClient(), combiner, new FakeSummarizationSettingsResolver(), hub, parent);

        var summary = await db.SectionSummaries.SingleAsync(x => x.SectionId == parent.Id);
        Assert.Equal("Folder-level summary.", summary.Text);
        Assert.Equal(SectionGenerationStatus.Ready, summary.Status);
        // Both recordings' summaries were fed to the combiner.
        Assert.Contains("Alpha summary.", combiner.LastMessages![1].Content);
        Assert.Contains("Beta summary.", combiner.LastMessages![1].Content);
        var msg = Assert.Single(hub.Sent);
        Assert.Equal("SectionStatusChanged", msg.Method);
        Assert.Equal(userId.ToString(), msg.Group);
    }

    [Fact]
    public async Task Regenerates_only_the_missing_per_recording_summaries()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId);
        await SeedRecording(db, userId, section.Id, name: "Has", summaryText: "Existing summary.");
        await SeedRecording(db, userId, section.Id, name: "Missing"); // no summary yet
        var perRec = new FakeSummarizationClient { Result = new SummaryResult("Freshly generated.", null) };

        await Run(db, perRec, new FakeMeetingMinutesClient(), new FakeSummarizationSettingsResolver(), new FakeHubContext(), section);

        Assert.Equal(1, perRec.Calls); // only the recording missing a summary was (re)generated
        Assert.False(perRec.LastNeedName); // folder roll-up never renames the recording
        // The generated summary is persisted on that recording's transcription.
        Assert.Equal(2, await db.Summaries.CountAsync());
        Assert.Contains(await db.Summaries.ToListAsync(), s => s.Text == "Freshly generated.");
    }

    [Fact]
    public async Task Skips_when_section_summary_is_user_edited()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId);
        await SeedRecording(db, userId, section.Id, summaryText: "x");
        db.SectionSummaries.Add(new SectionSummary
        {
            Id = Guid.NewGuid(), SectionId = section.Id, Model = "user", Text = "my edit",
            IsUserEdited = true, Status = SectionGenerationStatus.Ready,
        });
        await db.SaveChangesAsync();
        var combiner = new FakeMeetingMinutesClient();

        await Run(db, new FakeSummarizationClient(), combiner, new FakeSummarizationSettingsResolver(), new FakeHubContext(), section);

        var summary = await db.SectionSummaries.SingleAsync(x => x.SectionId == section.Id);
        Assert.Equal("my edit", summary.Text); // preserved
        Assert.Equal(0, combiner.Calls);        // LLM never called
    }

    [Fact]
    public async Task Not_configured_marks_failed()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId);
        await SeedRecording(db, userId, section.Id, summaryText: "x");
        var resolver = new FakeSummarizationSettingsResolver { Config = new("", "", "m", 60) }; // disabled
        var hub = new FakeHubContext();

        await Run(db, new FakeSummarizationClient(), new FakeMeetingMinutesClient(), resolver, hub, section);

        var summary = await db.SectionSummaries.SingleAsync(x => x.SectionId == section.Id);
        Assert.Equal(SectionGenerationStatus.Failed, summary.Status);
        Assert.False(string.IsNullOrEmpty(summary.Error));
        Assert.Equal("SectionStatusChanged", Assert.Single(hub.Sent).Method);
    }

    [Fact]
    public async Task Combiner_error_marks_failed_with_message()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId);
        await SeedRecording(db, userId, section.Id, summaryText: "x");
        var combiner = new FakeMeetingMinutesClient { ThrowOnCall = new InvalidOperationException("LLM down") };

        await Run(db, new FakeSummarizationClient(), combiner, new FakeSummarizationSettingsResolver(), new FakeHubContext(), section);

        var summary = await db.SectionSummaries.SingleAsync(x => x.SectionId == section.Id);
        Assert.Equal(SectionGenerationStatus.Failed, summary.Status);
        Assert.Equal("LLM down", summary.Error);
    }

    [Fact]
    public async Task ProcessAsync_AttributesTheCall_ToTheSectionAndItsOwner()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId);
        await SeedRecording(db, userId, section.Id, summaryText: "Existing summary.");

        LlmCallKind? observedKind = null;
        Guid? observedSection = null;
        Guid? observedUser = null;
        var combiner = new FakeMeetingMinutesClient(onCall: () =>
        {
            observedKind = LlmCallScope.Active?.Kind;
            observedSection = LlmCallScope.Active?.SectionId;
            observedUser = LlmCallScope.Active?.UserId;
        });

        await Run(db, new FakeSummarizationClient(), combiner, new FakeSummarizationSettingsResolver(),
            new FakeHubContext(), section);

        Assert.Equal(LlmCallKind.SectionSummary, observedKind);
        Assert.Equal(section.Id, observedSection);
        Assert.Equal(userId, observedUser);
    }

    [Fact]
    public async Task Empty_folder_is_ready_with_no_text_and_no_llm_call()
    {
        using var db = TestDb.Create();
        var section = await SeedSection(db, Guid.NewGuid());
        var combiner = new FakeMeetingMinutesClient();

        await Run(db, new FakeSummarizationClient(), combiner, new FakeSummarizationSettingsResolver(), new FakeHubContext(), section);

        var summary = await db.SectionSummaries.SingleAsync(x => x.SectionId == section.Id);
        Assert.Equal(SectionGenerationStatus.Ready, summary.Status);
        Assert.Equal("", summary.Text);
        Assert.Equal(0, combiner.Calls);
    }
}
