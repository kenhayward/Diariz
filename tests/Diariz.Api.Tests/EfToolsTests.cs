using System.Text.Json;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Tools;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The EF-backed tools (action items, summary, attendees, talk time, segment context) over the
/// in-memory provider.</summary>
public class EfToolsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (Recording rec, Transcription tr) SeedRecording(
        DiarizDbContext db, Guid userId, string name, DateTimeOffset? createdAt = null, int version = 1)
    {
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = name, Name = name,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow, Status = RecordingStatus.Transcribed,
        };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = version };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        return (rec, tr);
    }

    [Fact]
    public async Task ListActionItems_GroupsByRecording_AndFiltersByActor()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (rec, _) = SeedRecording(db, me, "Standup");
        db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Ship widget", Actor = "Alice", Deadline = "Fri", Ordinal = 0 });
        db.RecordingActions.Add(new RecordingAction { Id = Guid.NewGuid(), RecordingId = rec.Id, Text = "Write docs", Actor = "Bob", Ordinal = 1 });
        await db.SaveChangesAsync();

        var all = await new ListActionItemsTool(db).ExecuteAsync(Args("{}"), new ChatToolContext(me, []), default);
        Assert.Contains("Ship widget (Actor: Alice) (Due: Fri)", all);
        Assert.Contains($"/recordings/{rec.Id}", all);

        var byActor = await new ListActionItemsTool(db).ExecuteAsync(Args("{\"actor\":\"alice\"}"), new ChatToolContext(me, []), default);
        Assert.Contains("Ship widget", byActor);
        Assert.DoesNotContain("Write docs", byActor);
    }

    [Fact]
    public async Task GetRecordingSummary_UsesCurrentVersion_ByName()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (rec, v1) = SeedRecording(db, me, "Planning", version: 1);
        var v2 = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 2 };
        db.Transcriptions.Add(v2);
        db.Summaries.Add(new Summary { Id = Guid.NewGuid(), TranscriptionId = v1.Id, Model = "m", Text = "OLD summary" });
        db.Summaries.Add(new Summary { Id = Guid.NewGuid(), TranscriptionId = v2.Id, Model = "m", Text = "NEW summary" });
        await db.SaveChangesAsync();

        var result = await new GetRecordingSummaryTool(db).ExecuteAsync(Args("{\"name\":\"plan\"}"), new ChatToolContext(me, []), default);
        Assert.Contains("NEW summary", result);
        Assert.DoesNotContain("OLD summary", result);
        Assert.Contains($"/recordings/{rec.Id}", result);
    }

    [Fact]
    public async Task WhoAttended_FiltersByDate_AndListsSpeakers()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (r1, _) = SeedRecording(db, me, "June meeting", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var (r2, _) = SeedRecording(db, me, "July meeting", new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = r1.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = r2.Id, Label = "SPEAKER_00", DisplayName = "Carol" });
        await db.SaveChangesAsync();

        var result = await new WhoAttendedTool(db).ExecuteAsync(
            Args("{\"from\":\"2026-06-15\"}"), new ChatToolContext(me, []), default);
        Assert.Contains("Carol", result);
        Assert.DoesNotContain("Alice", result); // June meeting is before the from-date
    }

    [Fact]
    public async Task SpeakerTalkTime_ComputesPercentages()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (rec, tr) = SeedRecording(db, me, "Chat");
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "Bob" });
        // Alice 9s, Bob 3s → 75% / 25%.
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 9000, Original = "a", Ordinal = 0 });
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_01", StartMs = 9000, EndMs = 12000, Original = "b", Ordinal = 1 });
        await db.SaveChangesAsync();

        var result = await new SpeakerTalkTimeTool(db).ExecuteAsync(
            Args("{\"scope\":\"current\"}"), new ChatToolContext(me, [rec.Id]), default);
        Assert.Contains("Alice: 00:09 (75%)", result);
        Assert.Contains("Bob: 00:03 (25%)", result);
    }

    [Fact]
    public async Task GetSegmentContext_ReturnsWindowAroundTime()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (rec, tr) = SeedRecording(db, me, "Long talk");
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        for (var i = 0; i < 6; i++)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00",
                StartMs = i * 10_000, EndMs = (i * 10_000) + 9000, Original = $"line {i}", Ordinal = i,
            });
        await db.SaveChangesAsync();

        // 25s falls in segment index 2 (20s–29s); window 1 → segments 1..3.
        var result = await new GetSegmentContextTool(db).ExecuteAsync(
            Args("{\"recording\":\"long\",\"time\":\"00:25\",\"window\":1}"), new ChatToolContext(me, []), default);
        Assert.Contains("line 1", result);
        Assert.Contains("line 2", result);
        Assert.Contains("line 3", result);
        Assert.DoesNotContain("line 0", result);
        Assert.DoesNotContain("line 4", result);
    }
}
