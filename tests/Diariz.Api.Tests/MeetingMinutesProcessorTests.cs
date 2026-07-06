using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>The processor orchestrates one meeting-minutes job: load + guards, then delegate generation to the
/// (template-driven) generator and persist. Generation itself is covered by the composer/strategy/generator tests.</summary>
public class MeetingMinutesProcessorTests
{
    private static async Task<(Recording rec, Transcription tr)> Seed(
        DiarizDbContext db, Guid userId, bool withSegments = true, Guid? meetingTypeId = null,
        RecordingStatus status = RecordingStatus.Summarized)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Name = "Named", Status = status, BlobKey = "k",
            MeetingTypeId = meetingTypeId,
            CreatedAt = new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero),
        };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        if (withSegments)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = 0, EndMs = 1000, Original = "Hi", Ordinal = 0
            });
        await db.SaveChangesAsync();
        return (rec, tr);
    }

    private static MeetingMinutesJob Job(Recording rec, Transcription tr) => new(rec.Id, tr.Id);

    [Fact]
    public async Task ProcessAsync_PersistsGeneratorOutput_ReusesConfig_PassesTypeAndActions_NotifiesWithoutChangingStatus()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var (rec, tr) = await Seed(db, userId, meetingTypeId: typeId);
        db.RecordingActions.Add(new RecordingAction
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Send report", Actor = "Bob", Deadline = "", Ordinal = 0,
        });
        await db.SaveChangesAsync();

        var generator = new FakeMeetingTypeMinutesGenerator { Result = "# Cadence Call\n\nMinutes." };
        var resolver = new FakeSummarizationSettingsResolver();
        var hub = new FakeHubContext();

        await MeetingMinutesProcessor.ProcessAsync(
            db, generator, resolver, hub, Job(rec, tr), charBudget: 16000, NullLogger.Instance);

        var minutes = await db.MeetingMinutes.SingleAsync(m => m.TranscriptionId == tr.Id);
        Assert.Equal("# Cadence Call\n\nMinutes.", minutes.Text);
        Assert.Equal("test-model", minutes.Model);                 // from the resolved config
        Assert.Equal(userId, resolver.LastUserId);                 // resolved for the owner
        Assert.Equal(userId, generator.LastOwnerId);               // and passed to the generator
        Assert.Equal(typeId, generator.LastMeetingTypeId);         // the recording's chosen type
        Assert.Equal(resolver.Config, generator.LastConfig);       // the resolved config, straight through
        Assert.Equal("Send report", Assert.Single(generator.LastActions!).Text); // canonical actions handed over

        var reloaded = await db.Recordings.FindAsync(rec.Id);
        Assert.Equal(RecordingStatus.Summarized, reloaded!.Status); // status untouched (no race with summary)

        var msg = Assert.Single(hub.Sent);
        Assert.Equal(userId.ToString(), msg.Group);
        Assert.Equal("RecordingStatusChanged", msg.Method);
    }

    [Fact]
    public async Task ProcessAsync_UpdatesExistingMinutes()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        db.MeetingMinutes.Add(new MeetingMinutes
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "old", Text = "stale",
        });
        await db.SaveChangesAsync();

        await MeetingMinutesProcessor.ProcessAsync(
            db, new FakeMeetingTypeMinutesGenerator { Result = "# Fresh" }, new FakeSummarizationSettingsResolver(),
            new FakeHubContext(), Job(rec, tr), 16000, NullLogger.Instance);

        Assert.Equal("# Fresh", (await db.MeetingMinutes.SingleAsync(m => m.TranscriptionId == tr.Id)).Text);
    }

    [Fact]
    public async Task ProcessAsync_SkipsOverwrite_WhenMinutesUserEdited()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        db.MeetingMinutes.Add(new MeetingMinutes
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "user", Text = "my edit", IsUserEdited = true,
        });
        await db.SaveChangesAsync();
        var generator = new FakeMeetingTypeMinutesGenerator { Result = "# LLM" };

        await MeetingMinutesProcessor.ProcessAsync(
            db, generator, new FakeSummarizationSettingsResolver(), new FakeHubContext(),
            Job(rec, tr), 16000, NullLogger.Instance);

        Assert.Equal("my edit", (await db.MeetingMinutes.SingleAsync(m => m.TranscriptionId == tr.Id)).Text);
        Assert.Equal(0, generator.Calls); // generator never called
    }

    [Fact]
    public async Task ProcessAsync_OnGeneratorError_DoesNotThrow_LeavesStatusAndNoMinutes()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid());
        var generator = new FakeMeetingTypeMinutesGenerator { ThrowOnCall = new InvalidOperationException("LLM down") };

        await MeetingMinutesProcessor.ProcessAsync(
            db, generator, new FakeSummarizationSettingsResolver(), new FakeHubContext(),
            Job(rec, tr), 16000, NullLogger.Instance);

        Assert.Empty(await db.MeetingMinutes.ToListAsync());
        Assert.Equal(RecordingStatus.Summarized, (await db.Recordings.FindAsync(rec.Id))!.Status);
    }

    [Fact]
    public async Task ProcessAsync_NoSegments_DoesNothing()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await Seed(db, Guid.NewGuid(), withSegments: false);
        var generator = new FakeMeetingTypeMinutesGenerator();

        await MeetingMinutesProcessor.ProcessAsync(
            db, generator, new FakeSummarizationSettingsResolver(), new FakeHubContext(),
            Job(rec, tr), 16000, NullLogger.Instance);

        Assert.Equal(0, generator.Calls);
        Assert.Empty(await db.MeetingMinutes.ToListAsync());
    }
}
