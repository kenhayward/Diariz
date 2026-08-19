using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>The merge helper called directly, without a controller. It is shared by the on-demand Merge
/// action and the worker callback, so it is tested at its own seam rather than only through one caller.</summary>
public class TranscriptSegmentMergeTests
{
    private static async Task<(Guid recordingId, Guid transcriptionId)> Seed(
        Diariz.Domain.DiarizDbContext db, params (string label, long startMs, long endMs, string text)[] segments)
    {
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        var transcriptionId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t", BlobKey = "k" });
        db.Transcriptions.Add(new Transcription { Id = transcriptionId, RecordingId = recordingId, Model = "m", Version = 1 });
        var ordinal = 0;
        foreach (var (label, startMs, endMs, text) in segments)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = transcriptionId, SpeakerLabel = label,
                StartMs = startMs, EndMs = endMs, Original = text, Ordinal = ordinal++,
            });
        await db.SaveChangesAsync();
        return (recordingId, transcriptionId);
    }

    [Fact]
    public async Task ApplyAsync_CollapsesConsecutiveSameSpeaker_AndReportsTheChange()
    {
        using var db = TestDb.Create();
        var (recordingId, transcriptionId) = await Seed(db,
            ("SPEAKER_00", 0, 1000, "Hello"),
            ("SPEAKER_00", 1000, 2000, "World"));

        var changed = await TranscriptSegmentMerge.ApplyAsync(db, recordingId, transcriptionId);
        await db.SaveChangesAsync();

        Assert.True(changed);
        var seg = Assert.Single(await db.Segments.Where(s => s.TranscriptionId == transcriptionId).ToListAsync());
        Assert.Equal("Hello\nWorld", seg.EffectiveText);
        Assert.Equal(0, seg.StartMs);
        Assert.Equal(2000, seg.EndMs);
        Assert.Equal(0, seg.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_WithNothingAdjacent_ReportsNoChange_AndLeavesSegmentsAlone()
    {
        using var db = TestDb.Create();
        var (recordingId, transcriptionId) = await Seed(db,
            ("SPEAKER_00", 0, 1000, "Hello"),
            ("SPEAKER_01", 1000, 2000, "Hi there"));

        var changed = await TranscriptSegmentMerge.ApplyAsync(db, recordingId, transcriptionId);

        Assert.False(changed);
        Assert.Equal(2, await db.Segments.CountAsync(s => s.TranscriptionId == transcriptionId));
    }

    [Fact]
    public async Task ApplyAsync_WithNoSegments_ReportsNoChange()
    {
        using var db = TestDb.Create();
        var (recordingId, transcriptionId) = await Seed(db);

        Assert.False(await TranscriptSegmentMerge.ApplyAsync(db, recordingId, transcriptionId));
    }
}
