using Diariz.Api.Contracts;
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

    /// <summary>Seed with word timings, so the rebuild that a merge performs can be checked for carrying
    /// them. Separate from <c>Seed</c> because most tests here do not care about words.</summary>
    private static async Task<(Guid recordingId, Guid transcriptionId)> SeedWithWords(
        Diariz.Domain.DiarizDbContext db,
        params (string label, long startMs, long endMs, string text, string? revised, SegmentWord[]? words)[] segments)
    {
        var userId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        var transcriptionId = Guid.NewGuid();
        db.Recordings.Add(new Recording { Id = recordingId, UserId = userId, Title = "t", BlobKey = "k" });
        db.Transcriptions.Add(new Transcription { Id = transcriptionId, RecordingId = recordingId, Model = "m", Version = 1 });
        var ordinal = 0;
        foreach (var (label, startMs, endMs, text, revised, words) in segments)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = transcriptionId, SpeakerLabel = label,
                StartMs = startMs, EndMs = endMs, Original = text, Revised = revised,
                WordsJson = SegmentWords.Serialize(words), Ordinal = ordinal++,
            });
        await db.SaveChangesAsync();
        return (recordingId, transcriptionId);
    }

    /// <summary>Auto-merge runs on every transcription for users who enabled it, and it deletes and
    /// rebuilds every segment row. Word timings have to survive that, or merging silently destroys
    /// splittability - invisibly, because the transcript reads exactly the same either way.</summary>
    [Fact]
    public async Task ApplyAsync_CarriesWordsOntoTheMergedSegment()
    {
        using var db = TestDb.Create();
        var (recordingId, transcriptionId) = await SeedWithWords(db,
            ("SPEAKER_00", 0, 1000, "Hello", null, [new SegmentWord("Hello", 0, 1000)]),
            ("SPEAKER_00", 1100, 2000, "world", null, [new SegmentWord("world", 1100, 2000)]));

        Assert.True(await TranscriptSegmentMerge.ApplyAsync(db, recordingId, transcriptionId));
        await db.SaveChangesAsync();

        var seg = Assert.Single(db.Segments.Where(s => s.TranscriptionId == transcriptionId));
        Assert.Equal(
            [new SegmentWord("Hello", 0, 1000), new SegmentWord("world", 1100, 2000)],
            SegmentWords.Parse(seg.WordsJson));
    }

    [Fact]
    public async Task ApplyAsync_DropsWordsForARunContainingAnEditedSegment()
    {
        // Merge writes EffectiveText into a fresh Original, so a revised segment's merged text is the
        // user's words while the timings describe the model's. Carrying them would let a split cut at a
        // boundary that is not present in the text being cut.
        using var db = TestDb.Create();
        var (recordingId, transcriptionId) = await SeedWithWords(db,
            ("SPEAKER_00", 0, 1000, "Hello", null, [new SegmentWord("Hello", 0, 1000)]),
            ("SPEAKER_00", 1100, 2000, "world", "entirely different text", [new SegmentWord("world", 1100, 2000)]));

        Assert.True(await TranscriptSegmentMerge.ApplyAsync(db, recordingId, transcriptionId));
        await db.SaveChangesAsync();

        var seg = Assert.Single(db.Segments.Where(s => s.TranscriptionId == transcriptionId));
        Assert.Null(seg.WordsJson);
    }

    [Fact]
    public async Task ApplyAsync_DropsWordsWhenOnePartOfTheRunHasNone()
    {
        using var db = TestDb.Create();
        var (recordingId, transcriptionId) = await SeedWithWords(db,
            ("SPEAKER_00", 0, 1000, "Hello", null, [new SegmentWord("Hello", 0, 1000)]),
            ("SPEAKER_00", 1100, 2000, "world", null, null));

        Assert.True(await TranscriptSegmentMerge.ApplyAsync(db, recordingId, transcriptionId));
        await db.SaveChangesAsync();

        var seg = Assert.Single(db.Segments.Where(s => s.TranscriptionId == transcriptionId));
        Assert.Null(seg.WordsJson);
    }

    [Fact]
    public async Task ApplyAsync_LeavesAnUnmergedSegmentsWordsAlone()
    {
        // Two different speakers do not merge, so each row is rewritten with only its own words. A bug
        // that concatenated across the boundary would be invisible in the text.
        using var db = TestDb.Create();
        var (recordingId, transcriptionId) = await SeedWithWords(db,
            ("SPEAKER_00", 0, 1000, "Hello", null, [new SegmentWord("Hello", 0, 1000)]),
            ("SPEAKER_00", 1100, 2000, "there", null, [new SegmentWord("there", 1100, 2000)]),
            ("SPEAKER_01", 2100, 3000, "world", null, [new SegmentWord("world", 2100, 3000)]));

        Assert.True(await TranscriptSegmentMerge.ApplyAsync(db, recordingId, transcriptionId));
        await db.SaveChangesAsync();

        var segs = db.Segments.Where(s => s.TranscriptionId == transcriptionId).OrderBy(s => s.Ordinal).ToList();
        Assert.Equal(2, segs.Count);
        Assert.Equal(2, SegmentWords.Parse(segs[0].WordsJson).Count);
        Assert.Equal([new SegmentWord("world", 2100, 3000)], SegmentWords.Parse(segs[1].WordsJson));
    }
}
