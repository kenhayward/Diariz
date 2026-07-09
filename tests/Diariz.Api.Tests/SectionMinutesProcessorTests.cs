using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MinutesEntity = Diariz.Domain.Entities.MeetingMinutes;

namespace Diariz.Api.Tests;

/// <summary>Orchestration of a folder-minutes job: regenerate only the recordings missing individual minutes,
/// then reshape all the minutes through the folder's chosen meeting-type template onto the section.</summary>
public class SectionMinutesProcessorTests
{
    private static async Task<Section> SeedSection(DiarizDbContext db, Guid userId, Guid? parentId = null,
        Guid? meetingTypeId = null)
    {
        var s = new Section { Id = Guid.NewGuid(), UserId = userId, Name = "Folder", ParentId = parentId };
        db.Sections.Add(s);
        if (meetingTypeId is not null)
            db.SectionMinutes.Add(new SectionMinutes { Id = Guid.NewGuid(), SectionId = s.Id, MeetingTypeId = meetingTypeId });
        await db.SaveChangesAsync();
        return s;
    }

    private static async Task<Recording> SeedRecording(
        DiarizDbContext db, Guid userId, Guid? sectionId, string? name = null, string? minutesText = null,
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
        if (minutesText is not null)
            db.MeetingMinutes.Add(new MinutesEntity { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "m", Text = minutesText });
        await db.SaveChangesAsync();
        return rec;
    }

    private static Task Run(DiarizDbContext db, IMeetingTypeMinutesGenerator gen, IMeetingMinutesClient combiner,
        FakeSummarizationSettingsResolver resolver, FakeHubContext hub, Section section) =>
        SectionMinutesProcessor.ProcessAsync(db, gen, combiner, resolver, hub,
            FolderMinutesPrompt.DefaultTemplate, new SectionMinutesJob(section.Id), 16_000, 32_000, NullLogger.Instance);

    [Fact]
    public async Task Combines_minutes_across_section_and_children()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var parent = await SeedSection(db, userId);
        var child = await SeedSection(db, userId, parent.Id);
        await SeedRecording(db, userId, parent.Id, name: "A", minutesText: "# A\nAlpha minutes.");
        await SeedRecording(db, userId, child.Id, name: "B", minutesText: "# B\nBeta minutes.");
        var combiner = new FakeMeetingMinutesClient { Result = "# Folder minutes" };
        var hub = new FakeHubContext();

        await Run(db, new FakeMeetingTypeMinutesGenerator(), combiner, new FakeSummarizationSettingsResolver(), hub, parent);

        var minutes = await db.SectionMinutes.SingleAsync(x => x.SectionId == parent.Id);
        Assert.Equal("# Folder minutes", minutes.Text);
        Assert.Equal(SectionGenerationStatus.Ready, minutes.Status);
        Assert.Contains("Alpha minutes.", combiner.LastMessages![1].Content);
        Assert.Contains("Beta minutes.", combiner.LastMessages![1].Content);
        Assert.Equal("SectionStatusChanged", Assert.Single(hub.Sent).Method);
    }

    [Fact]
    public async Task Regenerates_only_missing_per_recording_minutes()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId);
        await SeedRecording(db, userId, section.Id, minutesText: "# Existing");
        await SeedRecording(db, userId, section.Id); // no minutes yet
        var gen = new FakeMeetingTypeMinutesGenerator { Result = "# Fresh minutes" };

        await Run(db, gen, new FakeMeetingMinutesClient(), new FakeSummarizationSettingsResolver(), new FakeHubContext(), section);

        Assert.Equal(1, gen.Calls); // only the recording missing minutes was generated
        Assert.Equal(2, await db.MeetingMinutes.CountAsync());
    }

    [Fact]
    public async Task Reshapes_through_the_folders_chosen_meeting_type()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var type = new MeetingType
        {
            Id = Guid.NewGuid(), UserId = userId, Title = "Retro", Overview = "A sprint retro.",
            ContentJson = new MeetingTypeContent([new TemplateSection(1, "What went well", [])]).Serialize(),
        };
        db.MeetingTypes.Add(type);
        await db.SaveChangesAsync();
        var section = await SeedSection(db, userId, meetingTypeId: type.Id);
        await SeedRecording(db, userId, section.Id, minutesText: "# R\nBody.");
        var combiner = new FakeMeetingMinutesClient();

        await Run(db, new FakeMeetingTypeMinutesGenerator(), combiner, new FakeSummarizationSettingsResolver(), new FakeHubContext(), section);

        Assert.Contains("Meeting type: Retro", combiner.LastMessages![0].Content);
        Assert.Contains("What went well", combiner.LastMessages![0].Content);
    }

    [Fact]
    public async Task Skips_when_section_minutes_are_user_edited()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId);
        await SeedRecording(db, userId, section.Id, minutesText: "x");
        db.SectionMinutes.Add(new SectionMinutes
        {
            Id = Guid.NewGuid(), SectionId = section.Id, Model = "user", Text = "hand edit",
            IsUserEdited = true, Status = SectionGenerationStatus.Ready,
        });
        await db.SaveChangesAsync();
        var combiner = new FakeMeetingMinutesClient();

        await Run(db, new FakeMeetingTypeMinutesGenerator(), combiner, new FakeSummarizationSettingsResolver(), new FakeHubContext(), section);

        Assert.Equal("hand edit", (await db.SectionMinutes.SingleAsync(x => x.SectionId == section.Id)).Text);
        Assert.Equal(0, combiner.Calls);
    }

    [Fact]
    public async Task Combiner_error_marks_failed()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var section = await SeedSection(db, userId);
        await SeedRecording(db, userId, section.Id, minutesText: "x");
        var combiner = new FakeMeetingMinutesClient { ThrowOnCall = new InvalidOperationException("boom") };

        await Run(db, new FakeMeetingTypeMinutesGenerator(), combiner, new FakeSummarizationSettingsResolver(), new FakeHubContext(), section);

        var minutes = await db.SectionMinutes.SingleAsync(x => x.SectionId == section.Id);
        Assert.Equal(SectionGenerationStatus.Failed, minutes.Status);
        Assert.Equal("boom", minutes.Error);
    }
}
