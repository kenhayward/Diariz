using Diariz.Api.Contracts;
using Diariz.Api.Services.Llm;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>The async recording-path processor: resolve the owner's LLM config, build the formula's context,
/// stream a completion, and flip the pre-created <see cref="FormulaResult"/> row to Ready (or Failed), then
/// notify the hub. Mirrors <see cref="SectionSummaryProcessor"/> - static, fakes + in-memory DbContext.</summary>
public class FormulaRunProcessorTests
{
    private static async Task<Recording> SeedRecordingWithTranscript(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "R", BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 1000, Original = "Hello there, team.", Ordinal = 0,
        });
        await db.SaveChangesAsync();
        return rec;
    }

    private static async Task<(Formula formula, FormulaResult result)> SeedFormulaAndResult(
        DiarizDbContext db, Guid userId, Guid recordingId)
    {
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId,
            Name = "Key Decisions", ContentJson = TemplateContent.FromPrompt("Summarize the key decisions made.").Serialize(),
            Context = FormulaContext.Transcript, Enabled = true,
        };
        var result = new FormulaResult
        {
            Id = Guid.NewGuid(), RecordingId = recordingId, CreatedByUserId = userId,
            FormulaId = formula.Id, Name = formula.Name, Ordinal = 0,
            Status = FormulaRunStatus.Generating,
        };
        db.Formulas.Add(formula);
        db.FormulaResults.Add(result);
        await db.SaveChangesAsync();
        return (formula, result);
    }

    private static Task Run(
        DiarizDbContext db, FakeChatStreamClient chat, FakeLlmSettingsResolver resolver,
        FakeHubContext hub, FormulaRunJob job) =>
        FormulaRunProcessor.ProcessAsync(
            db, chat, resolver, hub, job, NullLogger.Instance, new CapturingWebhookPublisher(), "");

    // ---- Section (folder) map-reduce path ----

    private static Section SeedSection(DiarizDbContext db, Guid userId, Guid roomId)
    {
        var section = new Section { Id = Guid.NewGuid(), UserId = userId, RoomId = roomId, Name = "Folder" };
        db.Sections.Add(section);
        return section;
    }

    /// <summary>Places a recording in <paramref name="sectionId"/> within <paramref name="roomId"/>. When
    /// <paramref name="withContent"/> is true the recording gets a transcription + one segment (so its formula
    /// context is non-empty); otherwise it has no transcription and yields the empty-context fallback.</summary>
    private static async Task<Guid> SeedRecordingInFolder(
        DiarizDbContext db, Guid userId, Guid roomId, Guid sectionId, string title,
        bool withContent, DateTimeOffset createdAt)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = title, BlobKey = "k", CreatedAt = createdAt,
        };
        db.Recordings.Add(rec);
        if (withContent)
        {
            var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
            db.Transcriptions.Add(tr);
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 1000, Original = $"Content of {title}.", Ordinal = 0,
            });
        }
        db.RoomRecordings.Add(new RoomRecording
        {
            RoomId = roomId, RecordingId = rec.Id, SectionId = sectionId, IsMainRoom = true,
        });
        await db.SaveChangesAsync();
        return rec.Id;
    }

    private static async Task<(Formula formula, SectionFormulaResult result)> SeedFormulaAndSectionResult(
        DiarizDbContext db, Guid userId, Guid sectionId)
    {
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId,
            Name = "Key Decisions", ContentJson = TemplateContent.FromPrompt("Summarize the key decisions made.").Serialize(),
            Context = FormulaContext.Transcript, Enabled = true,
        };
        var result = new SectionFormulaResult
        {
            Id = Guid.NewGuid(), SectionId = sectionId, CreatedByUserId = userId,
            FormulaId = formula.Id, Name = formula.Name, Ordinal = 0,
            Status = FormulaRunStatus.Generating,
        };
        db.Formulas.Add(formula);
        db.SectionFormulaResults.Add(result);
        await db.SaveChangesAsync();
        return (formula, result);
    }

    [Fact]
    public async Task Section_run_two_recordings_maps_each_then_reduces()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var section = SeedSection(db, userId, roomId);
        await db.SaveChangesAsync();
        await SeedRecordingInFolder(db, userId, roomId, section.Id, "Alpha", withContent: true,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        await SeedRecordingInFolder(db, userId, roomId, section.Id, "Beta", withContent: true,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var (formula, result) = await SeedFormulaAndSectionResult(db, userId, section.Id);
        var chat = new FakeChatStreamClient { StreamRounds = ["MAP-A", "MAP-B", "REDUCE"] };
        var hub = new FakeHubContext();

        await Run(db, chat, new FakeLlmSettingsResolver(), hub,
            new FormulaRunJob(null, section.Id, result.Id, formula.Id, userId));

        // 2 map calls + 1 reduce call.
        Assert.Equal(3, chat.Calls);

        var persisted = await db.SectionFormulaResults.FindAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Equal(FormulaRunStatus.Ready, persisted!.Status);
        Assert.Equal("REDUCE", persisted.Text);
        Assert.Null(persisted.Error);

        // The reduce call (3rd) concatenates the per-meeting outputs under "## {name}" headings.
        var reduceUser = chat.AllStreamMessages[2][1].Content;
        Assert.Equal("Summarize the key decisions made.", chat.AllStreamMessages[2][0].Content);
        Assert.Contains("## Alpha", reduceUser);
        Assert.Contains("MAP-A", reduceUser);
        Assert.Contains("## Beta", reduceUser);
        Assert.Contains("MAP-B", reduceUser);

        var msg = Assert.Single(hub.Sent);
        Assert.Equal("FormulaResultStatusChanged", msg.Method);
        Assert.Equal(userId.ToString(), msg.Group);
    }

    [Fact]
    public async Task Section_run_single_recording_skips_reduce()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var section = SeedSection(db, userId, roomId);
        await db.SaveChangesAsync();
        await SeedRecordingInFolder(db, userId, roomId, section.Id, "Alpha", withContent: true,
            createdAt: DateTimeOffset.UtcNow);
        var (formula, result) = await SeedFormulaAndSectionResult(db, userId, section.Id);
        var chat = new FakeChatStreamClient { StreamRounds = ["ONLY"] };
        var hub = new FakeHubContext();

        await Run(db, chat, new FakeLlmSettingsResolver(), hub,
            new FormulaRunJob(null, section.Id, result.Id, formula.Id, userId));

        // A single meeting means one map call and NO reduce.
        Assert.Equal(1, chat.Calls);

        var persisted = await db.SectionFormulaResults.FindAsync(result.Id);
        Assert.Equal(FormulaRunStatus.Ready, persisted!.Status);
        Assert.Equal("ONLY", persisted.Text);
        Assert.Null(persisted.Error);
    }

    [Fact]
    public async Task Section_run_no_meetings_with_content_marks_failed()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var section = SeedSection(db, userId, roomId);
        await db.SaveChangesAsync();
        // A placement with no transcription -> empty context -> skipped, so nothing to run over.
        await SeedRecordingInFolder(db, userId, roomId, section.Id, "Empty", withContent: false,
            createdAt: DateTimeOffset.UtcNow);
        var (formula, result) = await SeedFormulaAndSectionResult(db, userId, section.Id);
        var chat = new FakeChatStreamClient();
        var hub = new FakeHubContext();

        await Run(db, chat, new FakeLlmSettingsResolver(), hub,
            new FormulaRunJob(null, section.Id, result.Id, formula.Id, userId));

        // The empty meeting must not consume a map call.
        Assert.Equal(0, chat.Calls);

        var persisted = await db.SectionFormulaResults.FindAsync(result.Id);
        Assert.Equal(FormulaRunStatus.Failed, persisted!.Status);
        Assert.Contains("No meetings", persisted.Error);
        Assert.True(string.IsNullOrEmpty(persisted.Text));

        var msg = Assert.Single(hub.Sent);
        Assert.Equal("FormulaResultStatusChanged", msg.Method);
    }

    [Fact]
    public async Task Section_run_chat_error_marks_failed_with_message()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var section = SeedSection(db, userId, roomId);
        await db.SaveChangesAsync();
        await SeedRecordingInFolder(db, userId, roomId, section.Id, "Alpha", withContent: true,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        await SeedRecordingInFolder(db, userId, roomId, section.Id, "Beta", withContent: true,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var (formula, result) = await SeedFormulaAndSectionResult(db, userId, section.Id);
        var chat = new FakeChatStreamClient { ThrowOnCall = new InvalidOperationException("LLM down") };
        var hub = new FakeHubContext();

        await Run(db, chat, new FakeLlmSettingsResolver(), hub,
            new FormulaRunJob(null, section.Id, result.Id, formula.Id, userId));

        var persisted = await db.SectionFormulaResults.FindAsync(result.Id);
        Assert.Equal(FormulaRunStatus.Failed, persisted!.Status);
        Assert.False(string.IsNullOrEmpty(persisted.Error));

        var msg = Assert.Single(hub.Sent);
        Assert.Equal("FormulaResultStatusChanged", msg.Method);
        Assert.Equal(userId.ToString(), msg.Group);
    }

    [Fact]
    public async Task Recording_run_succeeds_sets_ready_with_text_and_notifies()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var chat = new FakeChatStreamClient { Tokens = ["OUT", "PUT"] };
        var hub = new FakeHubContext();

        await Run(db, chat, new FakeLlmSettingsResolver(), hub,
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId));

        var persisted = await db.FormulaResults.FindAsync(result.Id);
        Assert.NotNull(persisted);
        Assert.Equal(FormulaRunStatus.Ready, persisted!.Status);
        Assert.Equal("OUTPUT", persisted.Text);
        Assert.Null(persisted.Error);

        // The formula prompt is the system message; the transcript flows in as the user message.
        Assert.NotNull(chat.LastMessages);
        Assert.Equal("system", chat.LastMessages![0].Role);
        Assert.Equal("Summarize the key decisions made.", chat.LastMessages[0].Content);
        Assert.Contains("Hello there, team.", chat.LastMessages[1].Content);

        var msg = Assert.Single(hub.Sent);
        Assert.Equal("FormulaResultStatusChanged", msg.Method);
        Assert.Equal(userId.ToString(), msg.Group);
    }

    [Fact]
    public async Task Recording_run_attributes_the_call_to_the_recording_and_its_owner()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);

        LlmCallKind? observedKind = null;
        Guid? observedRecording = null;
        Guid? observedUser = null;
        var chat = new FakeChatStreamClient(onCall: () =>
        {
            observedKind = LlmCallScope.Active?.Kind;
            observedRecording = LlmCallScope.Active?.RecordingId;
            observedUser = LlmCallScope.Active?.UserId;
        });

        await Run(db, chat, new FakeLlmSettingsResolver(), new FakeHubContext(),
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId));

        Assert.Equal(LlmCallKind.FormulaRun, observedKind);
        Assert.Equal(rec.Id, observedRecording);
        Assert.Equal(userId, observedUser);
    }

    /// <summary>The section (folder) map-reduce path has no single recording to target - the brief's table only
    /// covers the recording-scoped case. Since <see cref="LlmCallScope"/> already carries section fields for
    /// exactly this purpose (see SectionSummaryProcessor/SectionMinutesProcessor), the folder run is attributed
    /// to the SECTION instead of left as Kind=Unknown, which would otherwise mean every folder-scoped formula
    /// run went unattributed.</summary>
    [Fact]
    public async Task Section_run_attributes_the_call_to_the_section_and_its_owner()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var roomId = Guid.NewGuid();
        var section = SeedSection(db, userId, roomId);
        await db.SaveChangesAsync();
        await SeedRecordingInFolder(db, userId, roomId, section.Id, "Alpha", withContent: true,
            createdAt: DateTimeOffset.UtcNow);
        var (formula, result) = await SeedFormulaAndSectionResult(db, userId, section.Id);

        LlmCallKind? observedKind = null;
        Guid? observedSection = null;
        Guid? observedUser = null;
        var chat = new FakeChatStreamClient(onCall: () =>
        {
            observedKind = LlmCallScope.Active?.Kind;
            observedSection = LlmCallScope.Active?.SectionId;
            observedUser = LlmCallScope.Active?.UserId;
        });

        await Run(db, chat, new FakeLlmSettingsResolver(), new FakeHubContext(),
            new FormulaRunJob(null, section.Id, result.Id, formula.Id, userId));

        Assert.Equal(LlmCallKind.FormulaRun, observedKind);
        Assert.Equal(section.Id, observedSection);
        Assert.Equal(userId, observedUser);
    }

    [Fact]
    public async Task Chat_error_marks_failed_with_message_and_notifies()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var chat = new FakeChatStreamClient { ThrowOnCall = new InvalidOperationException("LLM down") };
        var hub = new FakeHubContext();

        await Run(db, chat, new FakeLlmSettingsResolver(), hub,
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId));

        var persisted = await db.FormulaResults.FindAsync(result.Id);
        Assert.Equal(FormulaRunStatus.Failed, persisted!.Status);
        Assert.False(string.IsNullOrEmpty(persisted.Error));

        var msg = Assert.Single(hub.Sent);
        Assert.Equal("FormulaResultStatusChanged", msg.Method);
    }

    [Fact]
    public async Task ProcessAsync_WhenShutdownCancelsMidCall_StillRecordsFailed()
    {
        // The API being stopped (redeploy, container restart) cancels the job's token mid-call. FailAsync is
        // the only thing that moves the result off Generating, and it read AND wrote with the same token - so
        // the row kept "Generating..." with nothing left to finish it, while the worker acked the entry.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        using var cts = new CancellationTokenSource();
        var chat = new FakeChatStreamClient(onCall: cts.Cancel)
        {
            ThrowOnCall = new OperationCanceledException("The operation was canceled."),
        };

        await FormulaRunProcessor.ProcessAsync(
            db, chat, new FakeLlmSettingsResolver(), new FakeHubContext(),
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId), NullLogger.Instance,
            new CapturingWebhookPublisher(), "", cts.Token);

        var persisted = await db.FormulaResults.FindAsync(result.Id);
        Assert.Equal(FormulaRunStatus.Failed, persisted!.Status);
    }

    [Fact]
    public async Task AbandonAsync_MarksARecordingResultFailed_RatherThanLeavingItGenerating()
    {
        // A message past the delivery cap is dropped by StreamReclaimer to stop a poison job killing worker
        // after worker. Dropped silently it leaves the result card showing "Generating..." for good.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var hub = new FakeHubContext();

        await FormulaRunProcessor.AbandonAsync(
            db, hub, new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId), NullLogger.Instance);

        var persisted = await db.FormulaResults.FindAsync(result.Id);
        Assert.Equal(FormulaRunStatus.Failed, persisted!.Status);
        Assert.False(string.IsNullOrWhiteSpace(persisted.Error));
        Assert.Equal("FormulaResultStatusChanged", Assert.Single(hub.Sent).Method);
    }

    [Fact]
    public async Task AbandonAsync_MarksASectionResultFailed_Too()
    {
        // The section path writes a different table, so it needs its own proof rather than being assumed
        // from the recording one.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        // No room/section rows needed: settling reaches the result row by its own id, never through the tree.
        var sectionId = Guid.NewGuid();
        var (formula, result) = await SeedFormulaAndSectionResult(db, userId, sectionId);
        var hub = new FakeHubContext();

        await FormulaRunProcessor.AbandonAsync(
            db, hub, new FormulaRunJob(null, sectionId, result.Id, formula.Id, userId), NullLogger.Instance);

        var persisted = await db.SectionFormulaResults.FindAsync(result.Id);
        Assert.Equal(FormulaRunStatus.Failed, persisted!.Status);
        Assert.False(string.IsNullOrWhiteSpace(persisted.Error));
    }

    [Fact]
    public async Task AbandonAsync_LeavesAResultThatHasSinceMovedOn_Alone()
    {
        // An abandoned message can be hours old, and the run may have been re-done since. Failing it then
        // reports a dead job against work that has succeeded.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        result.Status = FormulaRunStatus.Ready;
        result.Text = "done";
        await db.SaveChangesAsync();
        var hub = new FakeHubContext();

        await FormulaRunProcessor.AbandonAsync(
            db, hub, new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId), NullLogger.Instance);

        var persisted = await db.FormulaResults.FindAsync(result.Id);
        Assert.Equal(FormulaRunStatus.Ready, persisted!.Status);
        Assert.Empty(hub.Sent);
    }

    [Fact]
    public async Task Not_configured_marks_failed_and_never_calls_chat()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);
        var chat = new FakeChatStreamClient();
        var resolver = new FakeLlmSettingsResolver { Config = new("", "", "m", new LlmParameters { TimeoutSeconds = 60 }) }; // disabled
        var hub = new FakeHubContext();

        await Run(db, chat, resolver, hub, new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId));

        var persisted = await db.FormulaResults.FindAsync(result.Id);
        Assert.Equal(FormulaRunStatus.Failed, persisted!.Status);
        Assert.False(string.IsNullOrEmpty(persisted.Error));
        Assert.Equal(0, chat.Calls);

        var msg = Assert.Single(hub.Sent);
        Assert.Equal("FormulaResultStatusChanged", msg.Method);
    }

    // ---- $USERNAME on the async path ----

    /// <summary>The async path has no "current user" - it must take the name from job.UserId, which for an
    /// automatic meeting-type-triggered run is the recording's owner.</summary>
    [Fact]
    public async Task ProcessAsync_SubstitutesTheJobUsersName()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var rec = await SeedRecordingWithTranscript(db, userId);
        var (formula, result) = await SeedFormulaAndResult(db, userId, rec.Id);

        formula.ContentJson = TemplateContent.FromPrompt("Ask $USERNAME").Serialize();
        db.Users.Add(new ApplicationUser
        {
            Id = userId, UserName = "a@b.test", Email = "a@b.test", FullName = "Ignored Display Name",
        });
        db.People.Add(new Person { Id = Guid.NewGuid(), LinkedUserId = userId, Name = "Ada Lovelace" });
        await db.SaveChangesAsync();

        var chat = new FakeChatStreamClient();
        await Run(db, chat, new FakeLlmSettingsResolver(), new FakeHubContext(),
            new FormulaRunJob(rec.Id, null, result.Id, formula.Id, userId));

        Assert.Equal("Ask Ada Lovelace", chat.LastMessages![0].Content);
    }
}
