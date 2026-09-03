using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Tests;

/// <summary>
/// Persisting one transcribed live chunk onto the provisional transcription.
/// </summary>
public class LiveChunkCallbackTests
{
    private const string Secret = "s3cret";

    private static LiveChunkCallbackController Build(DiarizDbContext db, FakeHubContext? hub = null,
        string? secret = Secret) =>
        new(db, hub ?? new FakeHubContext(), Options.Create(new WorkerOptions { CallbackSecret = Secret }))
        {
            ControllerContext = secret is null
                ? Http.Context(Guid.NewGuid())
                : Http.Context(Guid.NewGuid(), ("X-Worker-Secret", secret)),
        };

    private static async Task<Recording> SeedLive(DiarizDbContext db, Guid userId)
    {
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

    private static LiveChunkResult Result(Recording rec, Guid transcriptionId, int sequence,
        params (string Speaker, long Start, long End, string Text)[] segments) =>
        new(rec.Id, transcriptionId, sequence, "en",
            segments.Select(s => new SegmentResult(s.Speaker, s.Start, s.End, s.Text)).ToList(),
            [new SpeakerEmbeddingResult("SPEAKER_00", Enumerable.Repeat(0.1f, 192).ToArray())]);

    [Fact]
    public async Task FirstChunk_CreatesTheProvisionalTranscription()
    {
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());

        await Build(db).LiveChunk(Result(rec, Guid.Empty, 0, ("SPEAKER_00", 0, 3000, "Shall we start")));

        var tr = await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id);
        Assert.True(tr.IsProvisional);
        Assert.Equal(1, tr.Version);
        Assert.Single(await db.Segments.Where(s => s.TranscriptionId == tr.Id).ToListAsync());
    }

    [Fact]
    public async Task LaterChunks_AppendToTheSameProvisionalTranscription()
    {
        // Not a new version per chunk: the provisional transcription is one growing document, and the
        // final pass writes the next version once.
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());
        var c = Build(db);

        await c.LiveChunk(Result(rec, Guid.Empty, 0, ("SPEAKER_00", 0, 3000, "one")));
        var trId = (await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id)).Id;
        await c.LiveChunk(Result(rec, trId, 1, ("SPEAKER_00", 3000, 6000, "two")));

        Assert.Single(await db.Transcriptions.Where(t => t.RecordingId == rec.Id).ToListAsync());
        Assert.Equal(2, await db.Segments.CountAsync(s => s.TranscriptionId == trId));
    }

    [Fact]
    public async Task ARedeliveredChunk_ReplacesItsSegments_RatherThanDuplicating()
    {
        // Redis streams are at-least-once, so this WILL happen in production. An append-only handler
        // produces a transcript with sentences repeated at random, which reads as a transcription bug
        // rather than a queue one and would be miserable to diagnose.
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());
        var c = Build(db);

        await c.LiveChunk(Result(rec, Guid.Empty, 0, ("SPEAKER_00", 0, 3000, "first attempt")));
        var trId = (await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id)).Id;
        await c.LiveChunk(Result(rec, trId, 0, ("SPEAKER_00", 0, 3000, "redelivered")));

        var segs = await db.Segments.Where(s => s.TranscriptionId == trId).ToListAsync();
        Assert.Single(segs);
        Assert.Equal("redelivered", segs[0].Original);
    }

    [Fact]
    public async Task Segments_AreOrderedByRecordingTime_NotArrivalOrder()
    {
        // Chunks can complete out of order under retry, and Ordinal has to reflect the meeting rather
        // than the queue - it is what every reader sorts by.
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());
        var c = Build(db);

        await c.LiveChunk(Result(rec, Guid.Empty, 0, ("SPEAKER_00", 0, 3000, "first")));
        var trId = (await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id)).Id;
        await c.LiveChunk(Result(rec, trId, 2, ("SPEAKER_00", 60_000, 63_000, "third")));
        await c.LiveChunk(Result(rec, trId, 1, ("SPEAKER_00", 30_000, 33_000, "second")));

        var texts = await db.Segments.Where(s => s.TranscriptionId == trId)
            .OrderBy(s => s.Ordinal).Select(s => s.Original).ToListAsync();
        Assert.Equal(["first", "second", "third"], texts);
    }

    [Fact]
    public async Task Segments_WithinOneChunk_AreAlsoOrderedByTime()
    {
        // Across chunks, sequence order and time order coincide, because chunks are contiguous - so the
        // out-of-order test above passes just as well against a sort by chunk sequence. This is the case
        // that separates them: a chunk whose own segments do not arrive in time order. Sorting by
        // recording time is the direct expression of "reading order"; sorting by chunk is a proxy that
        // only happens to agree.
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());

        await Build(db).LiveChunk(Result(rec, Guid.Empty, 0,
            ("SPEAKER_00", 6000, 9000, "later"),
            ("SPEAKER_01", 0, 3000, "earlier")));

        var trId = (await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id)).Id;
        var texts = await db.Segments.Where(s => s.TranscriptionId == trId)
            .OrderBy(s => s.Ordinal).Select(s => s.Original).ToListAsync();
        Assert.Equal(["earlier", "later"], texts);
    }

    [Fact]
    public async Task Ordinals_DoNotDependOnHowMuchOfTheMeetingCameBefore()
    {
        // The property that makes the per-chunk cost constant (#753). The old assignment read every
        // segment in the transcription and renumbered the lot on every chunk, so the work grew with the
        // meeting - quadratic over its length, and multiplied directly by any move to shorter chunks.
        //
        // Asserting the VALUES are stable would not catch it: renumbering produced the same numbers it
        // already had. What separates the two is whether the arriving chunk's ordinals can be worked out
        // from the chunk alone. If they can, the read is unnecessary by construction.
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());
        var c = Build(db);

        await c.LiveChunk(Result(rec, Guid.Empty, 0, ("SPEAKER_00", 0, 3000, "opening")));
        var trId = (await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id)).Id;

        // A lot of meeting happens.
        for (var seq = 1; seq <= 20; seq++)
        {
            await c.LiveChunk(Result(rec, trId, seq,
                ("SPEAKER_00", seq * 30_000, seq * 30_000 + 3000, $"chunk {seq} a"),
                ("SPEAKER_01", seq * 30_000 + 4000, seq * 30_000 + 7000, $"chunk {seq} b")));
        }

        var late = await db.Segments
            .Where(s => s.TranscriptionId == trId && s.ChunkSequence == 20)
            .OrderBy(s => s.StartMs).Select(s => s.Ordinal).ToListAsync();

        // Now the same chunk into a transcription with almost nothing in front of it.
        using var fresh = TestDb.Create();
        var rec2 = await SeedLive(fresh, Guid.NewGuid());
        var c2 = Build(fresh);
        await c2.LiveChunk(Result(rec2, Guid.Empty, 0, ("SPEAKER_00", 0, 3000, "opening")));
        var trId2 = (await fresh.Transcriptions.SingleAsync(t => t.RecordingId == rec2.Id)).Id;
        await c2.LiveChunk(Result(rec2, trId2, 20,
            ("SPEAKER_00", 600_000, 603_000, "chunk 20 a"),
            ("SPEAKER_01", 604_000, 607_000, "chunk 20 b")));

        var alone = await fresh.Segments
            .Where(s => s.TranscriptionId == trId2 && s.ChunkSequence == 20)
            .OrderBy(s => s.StartMs).Select(s => s.Ordinal).ToListAsync();

        Assert.Equal(late, alone);
    }

    [Fact]
    public async Task Ordinals_LeaveEarlierChunksAlone()
    {
        // The same property from the other side: a chunk arriving must not rewrite rows it has nothing
        // to say about.
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());
        var c = Build(db);

        await c.LiveChunk(Result(rec, Guid.Empty, 0, ("SPEAKER_00", 0, 3000, "first")));
        var trId = (await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id)).Id;
        var before = await db.Segments.Where(s => s.TranscriptionId == trId)
            .Select(s => new { s.Original, s.Ordinal }).ToListAsync();

        await c.LiveChunk(Result(rec, trId, 1, ("SPEAKER_00", 30_000, 33_000, "second")));

        var after = await db.Segments.Where(s => s.TranscriptionId == trId && s.ChunkSequence == 0)
            .Select(s => new { s.Original, s.Ordinal }).ToListAsync();
        Assert.Equal(before.Select(x => x.Ordinal), after.Select(x => x.Ordinal));
    }

    [Fact]
    public async Task Ordinals_AreTheSameOnARedelivery()
    {
        // Every chunk arrives at least once and may arrive twice. Ordinals derived from the chunk are
        // idempotent by construction; ones counted across a growing table were not obliged to be.
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());
        var c = Build(db);

        await c.LiveChunk(Result(rec, Guid.Empty, 0, ("SPEAKER_00", 0, 3000, "hello")));
        var trId = (await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id)).Id;
        await c.LiveChunk(Result(rec, trId, 1, ("SPEAKER_00", 30_000, 33_000, "world")));
        var first = await db.Segments.Where(s => s.TranscriptionId == trId)
            .OrderBy(s => s.StartMs).Select(s => s.Ordinal).ToListAsync();

        await c.LiveChunk(Result(rec, trId, 1, ("SPEAKER_00", 30_000, 33_000, "world")));

        var second = await db.Segments.Where(s => s.TranscriptionId == trId)
            .OrderBy(s => s.StartMs).Select(s => s.Ordinal).ToListAsync();
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Ordinals_RiseStrictlyWithRecordingTime_AcrossChunkBoundaries()
    {
        // What the ordinal band is FOR. Numbering each chunk from its own base only reads correctly if
        // the base is far enough apart that a chunk's later segments cannot land on the next chunk's
        // first - so this is the case with several segments either side of a boundary, which a
        // one-segment-per-chunk test cannot see.
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());
        var c = Build(db);

        await c.LiveChunk(Result(rec, Guid.Empty, 0,
            ("SPEAKER_00", 0, 3000, "a"), ("SPEAKER_01", 4000, 7000, "b"), ("SPEAKER_00", 8000, 11_000, "c")));
        var trId = (await db.Transcriptions.SingleAsync(t => t.RecordingId == rec.Id)).Id;
        await c.LiveChunk(Result(rec, trId, 1,
            ("SPEAKER_00", 12_000, 15_000, "d"), ("SPEAKER_01", 16_000, 19_000, "e")));

        var byTime = await db.Segments.Where(s => s.TranscriptionId == trId)
            .OrderBy(s => s.StartMs).Select(s => new { s.Original, s.Ordinal }).ToListAsync();

        Assert.Equal(["a", "b", "c", "d", "e"], byTime.Select(x => x.Original));
        // Strictly increasing, so a sort by Ordinal reproduces the meeting exactly rather than nearly.
        for (var i = 1; i < byTime.Count; i++)
            Assert.True(byTime[i].Ordinal > byTime[i - 1].Ordinal,
                $"ordinal {byTime[i].Ordinal} for \"{byTime[i].Original}\" does not follow {byTime[i - 1].Ordinal}");
    }

    [Fact]
    public async Task NoSegments_IsNormalMidMeeting_AndDoesNotFailTheRecording()
    {
        // Silence is not the whole-recording "no speech was detected" failure. Nobody talking for
        // thirty seconds is an ordinary meeting, and failing the recording for it would be absurd.
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());

        var result = await Build(db).LiveChunk(Result(rec, Guid.Empty, 0));

        Assert.IsType<OkResult>(result);
        Assert.Equal(RecordingStatus.Live, (await db.Recordings.SingleAsync(r => r.Id == rec.Id)).Status);
        Assert.Null((await db.Recordings.SingleAsync(r => r.Id == rec.Id)).Error);
    }

    [Fact]
    public async Task SpeakerEmbeddings_AreStored_ButNoLabelIsAssigned()
    {
        // Phase 2 keeps the vectors for phase 3 and shows nothing. Per-chunk labels are meaningless
        // across chunks, so naming anyone now would be worse than saying nothing.
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());

        await Build(db).LiveChunk(Result(rec, Guid.Empty, 0, ("SPEAKER_00", 0, 3000, "hello")));

        var speaker = await db.Speakers.SingleOrDefaultAsync(s => s.RecordingId == rec.Id);
        Assert.NotNull(speaker);
        Assert.Null(speaker!.PersonId);
        Assert.False(speaker.IdentifiedAuto);
    }

    [Fact]
    public async Task ItNotifiesTheOwner_SoTheOpenPageUpdates()
    {
        using var db = TestDb.Create();
        var me = Guid.NewGuid();
        var rec = await SeedLive(db, me);
        var hub = new FakeHubContext();

        await Build(db, hub).LiveChunk(Result(rec, Guid.Empty, 0, ("SPEAKER_00", 0, 3000, "hello")));

        Assert.Contains(hub.Sent, m => m.Method == "LiveTranscriptAppended");
    }

    [Fact]
    public async Task WrongSecret_IsUnauthorized_AndWritesNothing()
    {
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());

        var result = await Build(db, secret: "wrong")
            .LiveChunk(Result(rec, Guid.Empty, 0, ("SPEAKER_00", 0, 3000, "hello")));

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Empty(await db.Transcriptions.Where(t => t.RecordingId == rec.Id).ToListAsync());
    }

    [Fact]
    public async Task AChunkForARecordingThatHasFinished_IsIgnored()
    {
        // Finalise can land while a chunk is still in flight. Reviving a provisional transcription then
        // would leave it as the highest version, hiding the real transcript behind partial text.
        using var db = TestDb.Create();
        var rec = await SeedLive(db, Guid.NewGuid());
        rec.Status = RecordingStatus.Transcribed;
        await db.SaveChangesAsync();

        var result = await Build(db).LiveChunk(Result(rec, Guid.Empty, 0, ("SPEAKER_00", 0, 3000, "late")));

        Assert.IsType<OkResult>(result);
        Assert.Empty(await db.Transcriptions.Where(t => t.RecordingId == rec.Id).ToListAsync());
    }
}
