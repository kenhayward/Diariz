using Diariz.Api.Mcp;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

public class McpResourcesUriTests
{
    [Fact]
    public void Uris_RoundTrip()
    {
        var id = Guid.NewGuid();
        Assert.True(McpResources.TryParse(McpResources.TranscriptUri(id), out var a, out var ka));
        Assert.Equal(id, a);
        Assert.Equal("transcript", ka);

        Assert.True(McpResources.TryParse(McpResources.MinutesUri(id), out var b, out var kb));
        Assert.Equal(id, b);
        Assert.Equal("minutes", kb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/x")]
    [InlineData("diariz://recording/not-a-guid/transcript")]
    [InlineData("diariz://recording/" + "11111111-1111-1111-1111-111111111111" + "/bogus")]
    [InlineData("diariz://recording/11111111-1111-1111-1111-111111111111")]
    public void TryParse_RejectsBadUris(string? uri)
    {
        Assert.False(McpResources.TryParse(uri, out _, out _));
    }

    [Fact]
    public void TranscriptText_RendersHeadingAndTimestampedLines()
    {
        var segs = new List<Segment>
        {
            new() { SpeakerLabel = "SPEAKER_00", StartMs = 5000, EndMs = 6000, Original = "Hi", Ordinal = 0 },
            new() { SpeakerLabel = "SPEAKER_01", StartMs = 65000, EndMs = 66000, Original = "Bye", Ordinal = 1 },
        };
        var text = McpResources.TranscriptText(
            "Standup", DateTimeOffset.UnixEpoch, segs,
            new Dictionary<string, string> { ["SPEAKER_00"] = "Alice", ["SPEAKER_01"] = "Bob" });

        Assert.Contains("# Standup — 1970-01-01 00:00", text);
        Assert.Contains("[00:05] Alice: Hi", text);
        Assert.Contains("[01:05] Bob: Bye", text);
    }
}

public class McpResourceServiceTests
{
    private static (Recording rec, Transcription tr) Seed(DiarizDbContext db, Guid userId, string name)
    {
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Title = name, Name = name, CreatedAt = DateTimeOffset.UtcNow };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "m", Version = 1 };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        return (rec, tr);
    }

    [Fact]
    public async Task List_IncludesTranscript_AndMinutesWhenPresent_OwnerScoped()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();

        var (r1, t1) = Seed(db, me, "With minutes");
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = t1.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Original = "x", Ordinal = 0 });
        db.MeetingMinutes.Add(new MeetingMinutes { Id = Guid.NewGuid(), TranscriptionId = t1.Id, Model = "m", Text = "## Notes" });

        var (r2, t2) = Seed(db, me, "Transcript only");
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = t2.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Original = "y", Ordinal = 0 });

        Seed(db, me, "No transcript yet"); // no segments → not listed
        var (rOther, tOther) = Seed(db, other, "Someone else");
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = tOther.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Original = "z", Ordinal = 0 });
        await db.SaveChangesAsync();

        var list = await new McpResourceService(db).ListAsync(me, default);
        var uris = list.Select(i => i.Uri).ToHashSet();

        Assert.Contains(McpResources.TranscriptUri(r1.Id), uris);
        Assert.Contains(McpResources.MinutesUri(r1.Id), uris);
        Assert.Contains(McpResources.TranscriptUri(r2.Id), uris);
        Assert.DoesNotContain(McpResources.MinutesUri(r2.Id), uris);   // no minutes
        Assert.DoesNotContain(McpResources.TranscriptUri(rOther.Id), uris); // other user's
        Assert.All(list, i => Assert.DoesNotContain("No transcript yet", i.Name)); // empty transcript excluded
    }

    [Fact]
    public async Task Read_ReturnsTranscript_AndMinutes()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var (rec, tr) = Seed(db, me, "Budget");
        db.Speakers.Add(new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice" });
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 2000, EndMs = 3000, Original = "Opening", Ordinal = 0 });
        db.MeetingMinutes.Add(new MeetingMinutes { Id = Guid.NewGuid(), TranscriptionId = tr.Id, Model = "m", Text = "## Decisions\n- ship" });
        await db.SaveChangesAsync();

        var svc = new McpResourceService(db);
        var transcript = await svc.ReadAsync(me, McpResources.TranscriptUri(rec.Id), default);
        Assert.NotNull(transcript);
        Assert.Contains("[00:02] Alice: Opening", transcript!.Text);
        Assert.Equal("text/markdown", transcript.MimeType);

        var minutes = await svc.ReadAsync(me, McpResources.MinutesUri(rec.Id), default);
        Assert.NotNull(minutes);
        Assert.Contains("## Decisions", minutes!.Text);
    }

    [Fact]
    public async Task Read_OtherUsersRecording_ReturnsNull()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var (rec, tr) = Seed(db, me, "Private");
        db.Segments.Add(new Segment { Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_00", StartMs = 0, EndMs = 1000, Original = "secret", Ordinal = 0 });
        await db.SaveChangesAsync();

        Assert.Null(await new McpResourceService(db).ReadAsync(bob, McpResources.TranscriptUri(rec.Id), default));
    }

    [Fact]
    public async Task Read_UnknownUri_ReturnsNull()
    {
        using var db = TestDb.Create();
        Assert.Null(await new McpResourceService(db).ReadAsync(Guid.NewGuid(), "diariz://nope", default));
    }
}
