using System.Text.Json;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Tools;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The single-recording read tools added for the MCP surface (also usable in chat):
/// get_transcript, get_meeting_minutes, get_recording_details.</summary>
public class NewReadToolsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (Recording rec, Transcription tr) Seed(DiarizDbContext db, Guid userId, string name)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = name, Name = name, Source = RecordingSource.Upload,
            DurationMs = 125_000, Status = RecordingStatus.Transcribed, CreatedAt = DateTimeOffset.UtcNow,
        };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        return (rec, tr);
    }

    [Fact]
    public async Task GetTranscript_ReturnsSpeakerLabelledTimestampedLines_ByName()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (rec, tr) = Seed(db, me, "Budget review");
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 5000, EndMs = 8000, Original = "Kick-off", Ordinal = 0 });
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 8000, EndMs = 10000, Original = "Next item", Ordinal = 1 });
        await db.SaveChangesAsync();

        var result = await new GetTranscriptTool(db).ExecuteAsync(
            Args("{\"recording\":\"budget\"}"), new ChatToolContext(me, []), default);

        Assert.Contains("[00:05] Alice: Kick-off", result);
        Assert.Contains("[00:08] Alice: Next item", result);
        Assert.Contains($"/recordings/{rec.Id}", result);
    }

    [Fact]
    public async Task GetTranscript_ByRecordingId_Exact()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (rec, tr) = Seed(db, me, "Anything");
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Original = "Hello", Ordinal = 0 });
        await db.SaveChangesAsync();

        var result = await new GetTranscriptTool(db).ExecuteAsync(
            Args($"{{\"recording_id\":\"{rec.Id}\"}}"), new ChatToolContext(me, []), default);
        Assert.Contains("Hello", result);
    }

    [Fact]
    public void FormatTranscript_TruncatesToMaxChars()
    {
        var segs = Enumerable.Range(0, 50).Select(i => new Segment
        {
            SpeakerLabel = "SPEAKER_00", StartMs = i * 1000, EndMs = (i * 1000) + 900,
            Original = $"line number {i} with some words", Ordinal = i,
        }).ToList();

        var result = GetTranscriptTool.FormatTranscript(
            Guid.NewGuid(), "Rec", DateTimeOffset.UnixEpoch, segs,
            new Dictionary<string, string> { ["SPEAKER_00"] = "Alice" }, maxChars: 300);

        Assert.Contains("truncated", result);
        Assert.True(result.Length < 500);
    }

    [Fact]
    public async Task GetMeetingMinutes_ReturnsText_OrNoneNote()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (rec, tr) = Seed(db, me, "Weekly sync");
        await db.SaveChangesAsync();

        var none = await new GetMeetingMinutesTool(db).ExecuteAsync(
            Args("{\"recording\":\"weekly\"}"), new ChatToolContext(me, []), default);
        Assert.Contains("No meeting minutes", none);

        db.MeetingMinutes.Add(new MeetingMinutes { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "m", Text = "## Decisions\n- Ship it" });
        await db.SaveChangesAsync();

        var some = await new GetMeetingMinutesTool(db).ExecuteAsync(
            Args("{\"recording\":\"weekly\"}"), new ChatToolContext(me, []), default);
        Assert.Contains("## Decisions", some);
        Assert.Contains("Ship it", some);
    }

    [Fact]
    public async Task GetRecordingDetails_ReportsMetadataAndFlags()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (rec, tr) = Seed(db, me, "Retro");
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Original = "hi", Ordinal = 0 });
        db.Summaries.Add(new Summary { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "m", Text = "A summary" });
        db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Do it", Ordinal = 0 });
        await db.SaveChangesAsync();

        var result = await new GetRecordingDetailsTool(db).ExecuteAsync(
            Args("{\"recording\":\"retro\"}"), new ChatToolContext(me, []), default);

        Assert.Contains("Duration: 02:05", result);
        Assert.Contains("Source: Upload", result);
        Assert.Contains("Status: Transcribed", result);
        Assert.Contains("Speakers: Alice", result);
        Assert.Contains("Has summary: yes", result);
        Assert.Contains("Has meeting minutes: no", result);
        Assert.Contains("Action items: 1", result);
    }

    [Fact]
    public async Task ReadTools_AreOwnerScoped()
    {
        using var db = TestDb.Create();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var (rec, tr) = Seed(db, alice, "Private");
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Original = "secret", Ordinal = 0 });
        await db.SaveChangesAsync();

        var result = await new GetTranscriptTool(db).ExecuteAsync(
            Args($"{{\"recording_id\":\"{rec.Id}\"}}"), new ChatToolContext(bob, []), default);
        Assert.DoesNotContain("secret", result);
        Assert.Contains("No matching recording", result);
    }
}
