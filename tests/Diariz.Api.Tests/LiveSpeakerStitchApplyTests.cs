using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>
/// Applying the stitch: turning a chunk's local diarization labels into labels that mean the same thing
/// for the whole meeting, and keeping a running centroid per voice.
/// </summary>
public class LiveSpeakerStitchApplyTests
{
    private const string Secret = "s3cret";

    private static LiveChunkCallbackController Build(DiarizDbContext db, FakeHubContext? hub = null) =>
        new(db, hub ?? new FakeHubContext(),
            Options.Create(new WorkerOptions { CallbackSecret = Secret }),
            Options.Create(new LiveCaptureOptions()))
        {
            ControllerContext = Http.Context(Guid.NewGuid(), ("X-Worker-Secret", Secret)),
        };

    private static async Task<Recording> SeedLive(DiarizDbContext db)
    {
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = "Standup", BlobKey = "",
            Status = RecordingStatus.Live, LiveSessionId = Guid.NewGuid(),
        };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec;
    }

    /// A unit vector at <paramref name="degrees"/> in the first two dimensions, so distances are known.
    private static float[] At(double degrees)
    {
        var r = degrees * Math.PI / 180.0;
        var v = new float[192];
        v[0] = (float)Math.Cos(r);
        v[1] = (float)Math.Sin(r);
        return v;
    }

    private static LiveChunkResult Chunk(
        Recording rec, int sequence,
        (string Label, double Degrees, long Start, long End, string Text)[] speakers) =>
        new(rec.Id, Guid.Empty, sequence, "en",
            speakers.Select(s => new SegmentResult(s.Label, s.Start, s.End, s.Text)).ToList(),
            speakers.Select(s => new SpeakerEmbeddingResult(s.Label, At(s.Degrees)))
                .DistinctBy(s => s.Speaker).ToList());

    private static Task<List<Segment>> Segments(DiarizDbContext db, Guid recordingId) =>
        db.Segments.Where(s => s.Transcription!.RecordingId == recordingId)
            .OrderBy(s => s.StartMs).ToListAsync();

    [Fact]
    public async Task SegmentsAreStoredUnderTheSessionLabel_NotTheChunkLocalOne()
    {
        // The same voice at 0 degrees, called SPEAKER_00 in chunk 0 and SPEAKER_01 in chunk 1 - which is
        // exactly what pyannote does, since it clusters each chunk independently. Stored as-is, the
        // transcript would show one person becoming another halfway through.
        using var db = TestDb.Create();
        var rec = await SeedLive(db);

        await Build(db).LiveChunk(Chunk(rec, 0, [("SPEAKER_00", 0, 0, 3000, "Shall we start")]));
        await Build(db).LiveChunk(Chunk(rec, 1, [("SPEAKER_01", 4, 30_000, 33_000, "Yes lets")]));

        var labels = (await Segments(db, rec.Id)).Select(s => s.SpeakerLabel).ToList();
        Assert.Equal(2, labels.Count);
        Assert.Single(labels.Distinct());
    }

    [Fact]
    public async Task ADifferentVoiceKeepsItsOwnSessionLabel()
    {
        using var db = TestDb.Create();
        var rec = await SeedLive(db);

        await Build(db).LiveChunk(Chunk(rec, 0, [("SPEAKER_00", 0, 0, 3000, "Shall we start")]));
        await Build(db).LiveChunk(Chunk(rec, 1, [("SPEAKER_00", 90, 30_000, 33_000, "Different person")]));

        var labels = (await Segments(db, rec.Id)).Select(s => s.SpeakerLabel).Distinct().ToList();
        Assert.Equal(2, labels.Count);
    }

    [Fact]
    public async Task ASpeakerRowExistsPerSessionLabel_CarryingTheRunningCentroid()
    {
        using var db = TestDb.Create();
        var rec = await SeedLive(db);

        await Build(db).LiveChunk(Chunk(rec, 0, [("SPEAKER_00", 0, 0, 3000, "one")]));
        await Build(db).LiveChunk(Chunk(rec, 1, [("SPEAKER_00", 10, 30_000, 33_000, "two")]));

        var speaker = Assert.Single(await db.Speakers.Where(s => s.RecordingId == rec.Id).ToListAsync());
        Assert.NotNull(speaker.Embedding);
        // The centroid has moved toward the second observation rather than being replaced by it or
        // frozen at the first - it is a running mean, not a last-write-wins.
        var centroid = speaker.Embedding!.ToArray();
        Assert.True(LiveSpeakerStitcher.CosineDistance(centroid, At(0)) > 0, "it moved");
        Assert.True(LiveSpeakerStitcher.CosineDistance(centroid, At(10)) > 0, "it is not just the latest");
    }

    [Fact]
    public async Task ARedeliveredChunkDoesNotCountTwiceIntoTheCentroid()
    {
        // At-least-once delivery means this WILL happen in production. A centroid that absorbs the same
        // vector twice is quietly wrong in a way no single-chunk test would catch - it drags the voice
        // toward whichever chunk happened to be retried.
        using var db = TestDb.Create();
        var rec = await SeedLive(db);

        await Build(db).LiveChunk(Chunk(rec, 0, [("SPEAKER_00", 0, 0, 3000, "one")]));
        await Build(db).LiveChunk(Chunk(rec, 1, [("SPEAKER_00", 20, 30_000, 33_000, "two")]));
        var once = (await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id)).Embedding!.ToArray();

        // The same chunk again, exactly as Redis would redeliver it.
        await Build(db).LiveChunk(Chunk(rec, 1, [("SPEAKER_00", 20, 30_000, 33_000, "two")]));
        var twice = (await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id)).Embedding!.ToArray();

        Assert.Equal(0.0, LiveSpeakerStitcher.CosineDistance(once, twice), 9);
        Assert.Equal(2, (await Segments(db, rec.Id)).Count);
    }

    [Fact]
    public async Task AMergeRelabelsEarlierSegmentsRetroactively()
    {
        // The correction for eager minting. One voice, but its first chunk lands at 0 degrees and its
        // second at 50 - a cosine distance of 0.357, just outside the 0.35 threshold - so they mint
        // separately, which is exactly what a noisy short-window ECAPA vector does. A third chunk at 45
        // joins the second label and drags its centroid to about 47.5, which brings the two inside the
        // threshold and they are plainly one person.
        //
        // The segment that then has to move is chunk 0's - the oldest text in the transcript, already on
        // the user's screen under a label the server has just stopped believing in.
        using var db = TestDb.Create();
        var rec = await SeedLive(db);

        await Build(db).LiveChunk(Chunk(rec, 0, [("SPEAKER_00", 0, 0, 3000, "one")]));
        await Build(db).LiveChunk(Chunk(rec, 1, [("SPEAKER_00", 50, 30_000, 33_000, "two")]));
        Assert.Equal(2, (await Segments(db, rec.Id)).Select(s => s.SpeakerLabel).Distinct().Count());
        var firstLabel = (await Segments(db, rec.Id)).Single(s => s.ChunkSequence == 0).SpeakerLabel;

        await Build(db).LiveChunk(Chunk(rec, 2, [("SPEAKER_00", 45, 60_000, 63_000, "three")]));

        var segments = await Segments(db, rec.Id);
        Assert.Single(segments.Select(s => s.SpeakerLabel).Distinct());
        Assert.NotEqual(firstLabel, segments.Single(s => s.ChunkSequence == 0).SpeakerLabel);
        Assert.Single(await db.Speakers.Where(s => s.RecordingId == rec.Id).ToListAsync());
    }

    [Fact]
    public async Task ARetroactiveRelabelIsPushedToTheClient()
    {
        // The client is holding text on screen under the old label. Relabelling in the database without
        // telling it leaves the two disagreeing until something else happens to trigger a refetch.
        using var db = TestDb.Create();
        var rec = await SeedLive(db);
        var hub = new FakeHubContext();

        await Build(db, hub).LiveChunk(Chunk(rec, 0, [("SPEAKER_00", 0, 0, 3000, "one")]));
        await Build(db, hub).LiveChunk(Chunk(rec, 1, [("SPEAKER_00", 50, 30_000, 33_000, "two")]));
        await Build(db, hub).LiveChunk(Chunk(rec, 2, [("SPEAKER_00", 45, 60_000, 63_000, "three")]));

        Assert.Contains(hub.Sent, m => m.Method == "LiveTranscriptAppended");
    }

    [Fact]
    public async Task TwoVoicesInOneChunkNeverShareASessionLabel()
    {
        // pyannote already decided these are two people in this chunk. Whatever the centroids say, they
        // must not be filed under one name - that loses who said what.
        using var db = TestDb.Create();
        var rec = await SeedLive(db);

        await Build(db).LiveChunk(Chunk(rec, 0, [("SPEAKER_00", 0, 0, 3000, "one")]));
        await Build(db).LiveChunk(Chunk(rec, 1,
            [("SPEAKER_00", 2, 30_000, 33_000, "a"), ("SPEAKER_01", 5, 33_000, 36_000, "b")]));

        var chunkOne = (await Segments(db, rec.Id)).Where(s => s.ChunkSequence == 1).ToList();
        Assert.Equal(2, chunkOne.Select(s => s.SpeakerLabel).Distinct().Count());
    }
}
