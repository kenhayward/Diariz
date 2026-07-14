using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>Minutes are a formula run: the meeting type's <b>primary formula</b> supplies the template and declares
/// the context, and the type's <b>additional formulas</b> run in the same pipeline, landing in the recording's
/// Formulas tab.
///
/// <para>These pin the two rules that make automatic re-runs tenable: a run <b>replaces</b> that formula's previous
/// result rather than appending a duplicate, and the automatic pipeline <b>never overwrites a hand-edited</b>
/// one.</para></summary>
public class MinutesFormulaPipelineTests
{
    private static async Task<(Recording rec, Transcription tr)> SeedRecording(DiarizDbContext db, Guid userId)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = "R", BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        db.Segments.Add(new Segment
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
            StartMs = 0, EndMs = 1000, Original = "We shipped it.", Ordinal = 0,
        });
        await db.SaveChangesAsync();
        return (rec, tr);
    }

    private static Formula Extra(DiarizDbContext db, string name)
    {
        var f = new Formula
        {
            Id = Guid.NewGuid(), Scope = FormulaScope.Platform, Name = name,
            ContentJson = TemplateContent.FromPrompt("Do it.").Serialize(),
            Context = FormulaContext.Transcript, Enabled = true,
        };
        db.Formulas.Add(f);
        return f;
    }

    private static Task Run(DiarizDbContext db, FakeJobQueue queue, Recording rec, Transcription tr) =>
        MeetingMinutesProcessor.ProcessAsync(
            db, new FakeMeetingTypeMinutesGenerator { Result = "# Minutes" }, new FakeSummarizationSettingsResolver(),
            new FakeHubContext(), queue, new MeetingMinutesJob(rec.Id, tr.Id), 16000, NullLogger.Instance);

    /// <summary>Attach `formulas` to a meeting type and put the recording on it.</summary>
    private static async Task AttachAsync(DiarizDbContext db, Recording rec, params Formula[] formulas)
    {
        var type = MeetingTypes.With(db, TemplateContent.FromPrompt("Minutes."), title: "T");
        await db.SaveChangesAsync();

        var ordinal = 0;
        foreach (var f in formulas)
            db.MeetingTypeFormulas.Add(new MeetingTypeFormula
            {
                Id = Guid.NewGuid(), MeetingTypeId = type.Id, FormulaId = f.Id, Ordinal = ordinal++,
            });

        rec.MeetingTypeId = type.Id;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_types_additional_formulas_are_queued_after_the_minutes_are_saved()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await SeedRecording(db, userId);
        var a = Extra(db, "A");
        var b = Extra(db, "B");
        await AttachAsync(db, rec, a, b);
        var queue = new FakeJobQueue();

        await Run(db, queue, rec, tr);

        // The minutes landed...
        Assert.Equal("# Minutes", (await db.MeetingMinutes.SingleAsync()).Text);
        // ...and each additional formula got a queued run + a Generating row in the Formulas tab.
        Assert.Equal(2, queue.FormulaRunJobs.Count);
        Assert.Equal([a.Id, b.Id], queue.FormulaRunJobs.Select(j => j.FormulaId));
        Assert.Equal(2, await db.FormulaResults.Where(r => r.RecordingId == rec.Id).CountAsync());
    }

    [Fact]
    public async Task A_recording_with_no_meeting_type_queues_nothing()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await SeedRecording(db, Guid.NewGuid());
        var queue = new FakeJobQueue();

        await Run(db, queue, rec, tr);

        Assert.Empty(queue.FormulaRunJobs);
    }

    // Regenerating the minutes (applying a type, re-transcribing) re-fires the additional formulas. They must
    // REPLACE their previous result, or a re-transcribed recording accumulates duplicates forever.
    [Fact]
    public async Task Regenerating_replaces_the_previous_result_instead_of_appending_a_duplicate()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await SeedRecording(db, Guid.NewGuid());
        var a = Extra(db, "A");
        await AttachAsync(db, rec, a);

        await Run(db, new FakeJobQueue(), rec, tr);
        var first = await db.FormulaResults.SingleAsync(r => r.RecordingId == rec.Id);

        await Run(db, new FakeJobQueue(), rec, tr);

        var only = await db.FormulaResults.SingleAsync(r => r.RecordingId == rec.Id);
        Assert.Equal(first.Id, only.Id);        // same document, re-generated in place
        Assert.Equal(first.Ordinal, only.Ordinal);
    }

    // The hazard the replace rule creates: results are hand-editable. The AUTOMATIC pipeline must never destroy
    // the user's own words - exactly as the minutes themselves refuse to overwrite hand-edited minutes.
    [Fact]
    public async Task An_automatic_rerun_leaves_a_hand_edited_result_alone()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await SeedRecording(db, Guid.NewGuid());
        var a = Extra(db, "A");
        await AttachAsync(db, rec, a);

        await Run(db, new FakeJobQueue(), rec, tr);

        // The user edits the generated document.
        var result = await db.FormulaResults.SingleAsync(r => r.RecordingId == rec.Id);
        result.Text = "My own words.";
        result.Status = FormulaRunStatus.Ready;
        result.IsUserEdited = true;
        await db.SaveChangesAsync();

        var queue = new FakeJobQueue();
        await Run(db, queue, rec, tr);   // the minutes regenerate, so the pipeline re-fires

        var after = await db.FormulaResults.SingleAsync(r => r.RecordingId == rec.Id);
        Assert.Equal("My own words.", after.Text);          // untouched
        Assert.True(after.IsUserEdited);
        Assert.Equal(FormulaRunStatus.Ready, after.Status);  // not reset to Generating
        Assert.Empty(queue.FormulaRunJobs);                     // and no run was even queued
    }

    // An explicit manual run is different: the user asked for it, so it DOES replace their edit (and clears the
    // flag) - mirroring ApplyMeetingType, which clears IsUserEdited before regenerating the minutes.
    [Fact]
    public async Task An_explicit_run_does_replace_a_hand_edited_result()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedRecording(db, userId);
        var a = Extra(db, "A");
        await db.SaveChangesAsync();

        db.FormulaResults.Add(new FormulaResult
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, FormulaId = a.Id, Name = a.Name,
            Text = "My own words.", Ordinal = 0, Status = FormulaRunStatus.Ready, IsUserEdited = true,
        });
        await db.SaveChangesAsync();

        var row = await FormulaResultUpsert.ForRecordingAsync(db, rec.Id, a, userId, automatic: false);

        Assert.NotNull(row);
        Assert.False(row!.IsUserEdited);                       // the flag is cleared
        Assert.Equal(FormulaRunStatus.Generating, row.Status); // and it will be regenerated
    }

    // A Platform/Diariz formula can be disabled after a type started pointing at it (only the PRIMARY is protected
    // from that), so the pipeline must not try to run one that is unavailable.
    [Fact]
    public async Task A_disabled_additional_formula_is_skipped()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await SeedRecording(db, Guid.NewGuid());
        var off = Extra(db, "Off");
        off.Enabled = false;
        await AttachAsync(db, rec, off);
        var queue = new FakeJobQueue();

        await Run(db, queue, rec, tr);

        Assert.Empty(queue.FormulaRunJobs);
        Assert.Empty(await db.FormulaResults.ToListAsync());
    }
}
