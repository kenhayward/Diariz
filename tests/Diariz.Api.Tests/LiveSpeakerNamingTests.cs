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
/// Putting a name to a live voice, through the <b>same</b> ranking and the same rules the finished-recording
/// path uses. A parallel copy with its own numbers would mean an administrator calibrating identification
/// silently changed one of them and not the other.
/// </summary>
public class LiveSpeakerNamingTests
{
    private const string Secret = "s3cret";

    private static (LiveChunkCallbackController Controller, FakeSpeakerIdentifier Identifier)
        Build(DiarizDbContext db, FakeHubContext? hub = null, IdentificationThresholds? thresholds = null)
    {
        var identifier = new FakeSpeakerIdentifier();
        var controller = new LiveChunkCallbackController(
            db, hub ?? new FakeHubContext(),
            Options.Create(new WorkerOptions { CallbackSecret = Secret }),
            Options.Create(new LiveCaptureOptions()),
            new FakeSpeakerIdentification(identifier, thresholds))
        {
            ControllerContext = Http.Context(Guid.NewGuid(), ("X-Worker-Secret", Secret)),
        };
        return (controller, identifier);
    }

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

    private static float[] At(double degrees)
    {
        var r = degrees * Math.PI / 180.0;
        var v = new float[192];
        v[0] = (float)Math.Cos(r);
        v[1] = (float)Math.Sin(r);
        return v;
    }

    /// One chunk, one voice, with enough speech that MinSpeechMs is not what decides the outcome.
    private static LiveChunkResult Chunk(Recording rec, int sequence, double degrees = 0,
        long startMs = 0, long endMs = 20_000) =>
        new(rec.Id, Guid.Empty, sequence, "en",
            [new SegmentResult("SPEAKER_00", startMs, endMs, "something said at length")],
            [new SpeakerEmbeddingResult("SPEAKER_00", At(degrees))]);

    private static Task<Speaker> TheSpeaker(DiarizDbContext db, Guid recordingId) =>
        db.Speakers.SingleAsync(s => s.RecordingId == recordingId);

    [Fact]
    public async Task AVoiceThatMatchesAnEnrolledPersonIsNamed()
    {
        using var db = TestDb.Create();
        var rec = await SeedLive(db);
        var (controller, identifier) = Build(db);
        var person = Guid.NewGuid();
        identifier.Nearest(person, "Ada", 0.10);   // inside the 0.30 accept threshold

        await controller.LiveChunk(Chunk(rec, 0));

        var speaker = await TheSpeaker(db, rec.Id);
        Assert.Equal(person, speaker.PersonId);
        Assert.Equal("Ada", speaker.DisplayName);
        Assert.True(speaker.IdentifiedAuto);
    }

    [Fact]
    public async Task AMatchInTheConfirmBandIsOfferedRatherThanAsserted()
    {
        // Exactly as for a finished recording: a borderline distance produces a question, not a name.
        using var db = TestDb.Create();
        var rec = await SeedLive(db);
        var (controller, identifier) = Build(db);
        var person = Guid.NewGuid();
        identifier.Nearest(person, "Ada", 0.35);   // past accept (0.30), inside suggest (0.40)

        await controller.LiveChunk(Chunk(rec, 0));

        var speaker = await TheSpeaker(db, rec.Id);
        Assert.Null(speaker.PersonId);
        Assert.Equal(person, speaker.SuggestedPersonId);
        Assert.False(speaker.IdentifiedAuto);
    }

    [Fact]
    public async Task AVoiceThatMatchesNobodyIsLeftAnonymous()
    {
        using var db = TestDb.Create();
        var rec = await SeedLive(db);
        var (controller, identifier) = Build(db);
        identifier.Nearest(Guid.NewGuid(), "Ada", 0.90);

        await controller.LiveChunk(Chunk(rec, 0));

        var speaker = await TheSpeaker(db, rec.Id);
        Assert.Null(speaker.PersonId);
        Assert.Null(speaker.SuggestedPersonId);
        Assert.Equal(speaker.Label, speaker.DisplayName);
    }

    [Fact]
    public async Task TooLittleSpeechIsNotJudgedAtAll()
    {
        // The same MinSpeechMs rule the finished path applies. Accuracy climbs steeply up to 10-20 seconds,
        // so a confident number derived from a second and a half is confidence in noise - and a live chunk
        // is exactly where the temptation to score a very short turn arises.
        using var db = TestDb.Create();
        var rec = await SeedLive(db);
        var (controller, identifier) = Build(db);
        identifier.Nearest(Guid.NewGuid(), "Ada", 0.01);   // as close as it gets

        await controller.LiveChunk(Chunk(rec, 0, startMs: 0, endMs: 900));

        var speaker = await TheSpeaker(db, rec.Id);
        Assert.Null(speaker.PersonId);
        Assert.Null(speaker.SuggestedPersonId);
    }

    [Fact]
    public async Task AManuallyNamedSpeakerIsNeverOverridden()
    {
        // Someone in the meeting said who this is. That is the only ground truth in the room, and a
        // centroid built from thirty seconds does not get to overrule it.
        using var db = TestDb.Create();
        var rec = await SeedLive(db);
        var (controller, identifier) = Build(db);
        identifier.Nearest(Guid.NewGuid(), "Ada", 0.01);

        await controller.LiveChunk(Chunk(rec, 0));
        var speaker = await TheSpeaker(db, rec.Id);
        speaker.DisplayName = "Grace";
        speaker.PersonId = Guid.NewGuid();
        speaker.IdentifiedAuto = false;
        var chosen = speaker.PersonId;
        await db.SaveChangesAsync();

        await controller.LiveChunk(Chunk(rec, 1, startMs: 30_000, endMs: 50_000));

        var after = await TheSpeaker(db, rec.Id);
        Assert.Equal("Grace", after.DisplayName);
        Assert.Equal(chosen, after.PersonId);
    }

    [Fact]
    public async Task TheDecisionIsRetakenAsTheCentroidImproves()
    {
        // A voice named on one noisy chunk must be correctable by later evidence, or an early wrong guess
        // is permanent for the meeting. The name is applied on chunk 0 and withdrawn on chunk 1 when the
        // improved centroid no longer matches.
        using var db = TestDb.Create();
        var rec = await SeedLive(db);
        var (controller, identifier) = Build(db);
        identifier.Nearest(Guid.NewGuid(), "Ada", 0.10);

        await controller.LiveChunk(Chunk(rec, 0));
        Assert.NotNull((await TheSpeaker(db, rec.Id)).PersonId);

        identifier.Nearest(Guid.NewGuid(), "Ada", 0.95);
        await controller.LiveChunk(Chunk(rec, 1, degrees: 5, startMs: 30_000, endMs: 50_000));

        var after = await TheSpeaker(db, rec.Id);
        Assert.Null(after.PersonId);
        Assert.Equal(after.Label, after.DisplayName);
    }

    [Fact]
    public async Task IdentificationRunsOnTheStitchedCentroid_NotOnOneChunksVector()
    {
        // The whole point of the centroid: a voice is ranked against the directory using everything the
        // meeting has heard of it, not whichever thirty seconds arrived last.
        using var db = TestDb.Create();
        var rec = await SeedLive(db);
        var (controller, identifier) = Build(db);
        identifier.Nearest(Guid.NewGuid(), "Ada", 0.90);

        await controller.LiveChunk(Chunk(rec, 0, degrees: 0));
        await controller.LiveChunk(Chunk(rec, 1, degrees: 20, startMs: 30_000, endMs: 50_000));

        var speaker = await TheSpeaker(db, rec.Id);
        var centroid = speaker.Embedding!.ToArray();

        // Between the two observations rather than sitting on either.
        Assert.True(LiveSpeakerStitcher.CosineDistance(centroid, At(0)) > 0);
        Assert.True(LiveSpeakerStitcher.CosineDistance(centroid, At(20)) > 0);

        // And the centroid is what was actually ranked. Asserting only that ranking happened would pass
        // just as well against a version that handed the directory the latest chunk's raw vector, which
        // is the mistake this test exists to catch.
        Assert.Equal(0.0, LiveSpeakerStitcher.CosineDistance(identifier.LastProbe!, centroid), 9);
        Assert.Equal(2, identifier.Calls);
    }
}
