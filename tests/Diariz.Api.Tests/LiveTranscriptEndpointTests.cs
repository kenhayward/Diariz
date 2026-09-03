using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>
/// The narrow read the live transcript uses while a meeting runs.
///
/// <para>It exists because the browser used to refetch the WHOLE recording on every arriving chunk -
/// metadata, speakers, action items, the calendar link, the visible rooms, the summary and the meeting
/// minutes - to render a handful of new lines (issue #753). That is harmless at minute five and a large
/// payload every few seconds at minute ninety, and it caps how short chunks can usefully get.</para>
///
/// <para>It deliberately still returns the <b>whole</b> transcript rather than a delta: that is what
/// makes a missed hub event self-healing, and it is the property worth keeping from the old call.</para>
/// </summary>
public class LiveTranscriptEndpointTests
{
    private static async Task<(Recording Rec, Transcription Tr)> SeedLive(
        DiarizDbContext db, Guid userId, bool provisional = true)
    {
        Users.Ensure(db, userId);
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = "Standup", BlobKey = "",
            Status = RecordingStatus.Live, LiveSessionId = Guid.NewGuid(),
        };
        var tr = new Transcription
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx-live",
            Version = 1, IsProvisional = provisional,
        };
        db.Recordings.Add(rec);
        db.Transcriptions.Add(tr);
        await db.SaveChangesAsync();
        return (rec, tr);
    }

    private static Segment Seg(Transcription tr, string label, long startMs, string text, int ordinal) =>
        new()
        {
            Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = label,
            StartMs = startMs, EndMs = startMs + 3000, Original = text, Ordinal = ordinal,
        };

    [Fact]
    public async Task ReturnsTheTranscriptSoFar_InReadingOrder()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await SeedLive(db, userId);
        db.Segments.AddRange(
            Seg(tr, "SPEAKER_00", 30_000, "second", 10_000),
            Seg(tr, "SPEAKER_00", 0, "first", 0));
        await db.SaveChangesAsync();

        var result = await LiveTestSupport.Build(db, userId).LiveTranscript(rec.Id);

        var dto = Assert.IsType<LiveTranscriptDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(["first", "second"], dto.Segments.Select(s => s.Text));
        Assert.Equal(rec.Id, dto.RecordingId);
    }

    [Fact]
    public async Task PrefersTheEditedTextOverTheOriginal()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await SeedLive(db, userId);
        var seg = Seg(tr, "SPEAKER_00", 0, "as heard", 0);
        seg.Revised = "as corrected";
        db.Segments.Add(seg);
        await db.SaveChangesAsync();

        var result = await LiveTestSupport.Build(db, userId).LiveTranscript(rec.Id);

        var dto = (LiveTranscriptDto)((OkObjectResult)result.Result!).Value!;
        Assert.Equal("as corrected", dto.Segments[0].Text);
    }

    [Fact]
    public async Task NamesTheSpeaker_AndSaysWhenTheNameIsOnlyASuggestion()
    {
        // The browser used to work this out itself, by fetching every speaker on the recording and
        // matching labels. Resolving it here is the whole point: one call, already joined.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await SeedLive(db, userId);
        db.Speakers.AddRange(
            new Speaker { Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Ada" },
            new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "Grace",
                SuggestedPersonId = Guid.NewGuid(),
            });
        db.Segments.AddRange(
            Seg(tr, "SPEAKER_00", 0, "confident", 0),
            Seg(tr, "SPEAKER_01", 4000, "a guess", 1));
        await db.SaveChangesAsync();

        var result = await LiveTestSupport.Build(db, userId).LiveTranscript(rec.Id);

        var dto = (LiveTranscriptDto)((OkObjectResult)result.Result!).Value!;
        Assert.Equal("Ada", dto.Segments[0].Speaker);
        Assert.False(dto.Segments[0].SpeakerIsSuggestion);
        Assert.Equal("Grace", dto.Segments[1].Speaker);
        Assert.True(dto.Segments[1].SpeakerIsSuggestion);
    }

    [Fact]
    public async Task GivesNoSpeakerRatherThanARawLabel()
    {
        // An unstitched label means nothing to a reader - it is worse than no name at all, which is the
        // rule the panel already followed on the client.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, tr) = await SeedLive(db, userId);
        db.Segments.Add(Seg(tr, "SPEAKER_07", 0, "anonymous", 0));
        await db.SaveChangesAsync();

        var result = await LiveTestSupport.Build(db, userId).LiveTranscript(rec.Id);

        var dto = (LiveTranscriptDto)((OkObjectResult)result.Result!).Value!;
        Assert.Null(dto.Segments[0].Speaker);
    }

    [Fact]
    public async Task IsEmptyRatherThanMissingBeforeTheFirstChunkLands()
    {
        // A meeting that has started but not yet been transcribed is the normal opening state, not an
        // error - the panel shows "waiting for the first transcript" off the back of it.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, _) = await SeedLive(db, userId);

        var result = await LiveTestSupport.Build(db, userId).LiveTranscript(rec.Id);

        var dto = (LiveTranscriptDto)((OkObjectResult)result.Result!).Value!;
        Assert.Empty(dto.Segments);
    }

    [Fact]
    public async Task IsNotVisibleToSomebodyElse()
    {
        using var db = TestDb.Create();
        var (rec, tr) = await SeedLive(db, Guid.NewGuid());
        db.Segments.Add(Seg(tr, "SPEAKER_00", 0, "private", 0));
        await db.SaveChangesAsync();

        var stranger = Guid.NewGuid();
        Users.Ensure(db, stranger);
        var result = await LiveTestSupport.Build(db, stranger).LiveTranscript(rec.Id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ReadsTheFinishedTranscriptOnceTheMeetingHasEnded()
    {
        // The panel keeps reading this for a moment after Stop, while the final pass takes over. It must
        // follow the same "current transcription" rule as the detail endpoint rather than insisting on
        // the provisional one, or the text would blank out at exactly the wrong moment.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, provisional) = await SeedLive(db, userId);
        rec.Status = RecordingStatus.Transcribed;
        var final = new Transcription
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 2, IsProvisional = false,
        };
        db.Transcriptions.Add(final);
        db.Segments.Add(Seg(provisional, "SPEAKER_00", 0, "provisional text", 0));
        db.Segments.Add(Seg(final, "SPEAKER_00", 0, "final text", 0));
        await db.SaveChangesAsync();

        var result = await LiveTestSupport.Build(db, userId).LiveTranscript(rec.Id);

        var dto = (LiveTranscriptDto)((OkObjectResult)result.Result!).Value!;
        Assert.Equal(["final text"], dto.Segments.Select(s => s.Text));
    }

    [Fact]
    public async Task AnEmptyNewerTranscriptionDoesNotHideTheLiveText()
    {
        // The same rule the detail endpoint carries: the full pass takes the next version the moment its
        // job is queued, before a single segment is written. Letting that empty row win would wipe the
        // live text off the screen for as long as the full pass takes.
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        var (rec, provisional) = await SeedLive(db, userId);
        db.Transcriptions.Add(new Transcription
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Model = "whisperx", Version = 2, IsProvisional = false,
        });
        db.Segments.Add(Seg(provisional, "SPEAKER_00", 0, "everything said so far", 0));
        await db.SaveChangesAsync();

        var result = await LiveTestSupport.Build(db, userId).LiveTranscript(rec.Id);

        var dto = (LiveTranscriptDto)((OkObjectResult)result.Result!).Value!;
        Assert.Equal(["everything said so far"], dto.Segments.Select(s => s.Text));
    }
}
