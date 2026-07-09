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
    private static async Task<Section> SeedSection(DiarizDbContext db, Guid userId, Guid? parentId = null)
    {
        var s = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Folder", ParentId = parentId };
        db.Sections.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    /// <summary>A recording with a current transcription + one segment, optionally pre-loaded with a summary.</summary>
    private static async Task<Recording> SeedRecording(
        DiarizDbContext db, Guid userId, Guid? sectionId, string? name = null, string? summaryText = null,
        bool withSegments = true)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Name = name, SectionId = sectionId, BlobKey = "k" };
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
        return rec;
    }

    private static Task Run(DiarizDbContext db, ISummarizationClient perRec, IMeetingMinutesClient combiner,
        FakeSummarizationSettingsResolver resolver, FakeHubContext hub, Section section) =>
        SectionSummaryProcessor.ProcessAsync(db, perRec, combiner, resolver, hub,
            SummarizationPrompt.DefaultTemplate, FolderSummaryPrompt.DefaultTemplate,
            new SectionSummaryJob(section.Id), 24_000, NullLogger.Instance);

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
