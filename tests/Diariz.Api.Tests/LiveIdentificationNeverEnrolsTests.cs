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
/// The hard rule of the whole identification design: <b>an automatic match never enrols</b>.
///
/// <para>This has its own file because it is the one mistake in this phase that would damage other users'
/// data across the entire instance rather than this recording. Enrolment is platform-wide -
/// <c>PeopleDirectory.RecomputeVoiceprintAsync</c> averages every <c>VoiceSample</c> for a person with no
/// owner filter, and <c>ISpeakerIdentifier.RankAsync</c> scans every person with an embedding - so one bad
/// sample changes recognition for everybody, in every meeting, from then on.</para>
///
/// <para>A live chunk is the worst possible input to that: provisional text, a window of seconds, and a
/// centroid still forming from a diarization label that may yet be merged into another. The rule is
/// therefore not "be careful here" but "this path does not write training data at all".</para>
/// </summary>
public class LiveIdentificationNeverEnrolsTests
{
    private const string Secret = "s3cret";

    private static (LiveChunkCallbackController Controller, FakeSpeakerIdentifier Identifier)
        Build(DiarizDbContext db)
    {
        var identifier = new FakeSpeakerIdentifier();
        var controller = new LiveChunkCallbackController(
            db, new FakeHubContext(),
            Options.Create(new WorkerOptions { CallbackSecret = Secret }),
            Options.Create(new LiveCaptureOptions()),
            new FakeSpeakerIdentification(identifier))
        {
            ControllerContext = Http.Context(Guid.NewGuid(), ("X-Worker-Secret", Secret)),
        };
        return (controller, identifier);
    }

    private static async Task<(Recording Rec, Person Person)> SeedLiveWithEnrolledPerson(DiarizDbContext db)
    {
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId, Title = "Standup", BlobKey = "",
            Status = RecordingStatus.Live, LiveSessionId = Guid.NewGuid(),
        };
        var person = new Person { Id = Guid.NewGuid(), Name = "Ada" };
        db.Recordings.Add(rec);
        db.People.Add(person);
        await db.SaveChangesAsync();
        return (rec, person);
    }

    private static float[] At(double degrees)
    {
        var r = degrees * Math.PI / 180.0;
        var v = new float[192];
        v[0] = (float)Math.Cos(r);
        v[1] = (float)Math.Sin(r);
        return v;
    }

    private static LiveChunkResult Chunk(Recording rec, int sequence, double degrees = 0,
        long startMs = 0, long endMs = 20_000) =>
        new(rec.Id, Guid.Empty, sequence, "en",
            [new SegmentResult("SPEAKER_00", startMs, endMs, "something said at length")],
            [new SpeakerEmbeddingResult("SPEAKER_00", At(degrees))]);

    [Theory]
    // A confident match, a suggestion, and no match at all. All three must behave identically here:
    // the difference between them is what appears on screen, never what is written to the directory.
    [InlineData(0.05)]
    [InlineData(0.35)]
    [InlineData(0.95)]
    public async Task NoVoiceSampleIsWritten_WhateverTheVerdict(double distance)
    {
        using var db = TestDb.Create();
        var (rec, person) = await SeedLiveWithEnrolledPerson(db);
        var (controller, identifier) = Build(db);
        identifier.Nearest(person.Id, "Ada", distance);

        await controller.LiveChunk(Chunk(rec, 0));
        await controller.LiveChunk(Chunk(rec, 1, degrees: 3, startMs: 30_000, endMs: 50_000));

        Assert.Empty(await db.VoiceSamples.ToListAsync());
    }

    [Fact]
    public async Task NoSharedCentroidIsRebuilt_EvenOnAConfidentMatch()
    {
        using var db = TestDb.Create();
        var (rec, person) = await SeedLiveWithEnrolledPerson(db);
        var (controller, identifier) = Build(db);
        identifier.Nearest(person.Id, "Ada", 0.05);

        // The controller has no way to enrol: it takes neither IPeopleDirectory nor ISpeakerAssignment,
        // and that absence IS the guarantee. Asserted structurally rather than only by looking at the
        // table afterwards, because a test that checks the outcome can be satisfied by a path that simply
        // did not fire this time - whereas a dependency that is not there cannot be called at all.
        Assert.DoesNotContain(
            typeof(LiveChunkCallbackController).GetConstructors().Single().GetParameters(),
            p => p.ParameterType == typeof(IPeopleDirectory)
                 || p.ParameterType == typeof(ISpeakerAssignment));

        await controller.LiveChunk(Chunk(rec, 0));

        var speaker = await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id);
        Assert.Equal(person.Id, speaker.PersonId);      // it DID name them
        Assert.Empty(await db.VoiceSamples.ToListAsync());   // and it did NOT train on them
    }

    [Fact]
    public async Task APersonWithNoVoiceprintYetIsNotGivenOneByBeingMatched()
    {
        // The most tempting case: a person exists, the ranking is close, and there is no voiceprint to
        // damage - so "just enrol it" looks free. It is not: it seeds a platform-wide centroid from a
        // provisional thirty-second window, and every later recording is then matched against that.
        using var db = TestDb.Create();
        var (rec, person) = await SeedLiveWithEnrolledPerson(db);
        var (controller, identifier) = Build(db);
        identifier.Nearest(person.Id, "Ada", 0.05);

        await controller.LiveChunk(Chunk(rec, 0));

        var stored = await db.People.SingleAsync(p => p.Id == person.Id);
        Assert.Null(stored.Embedding);
        Assert.Empty(await db.VoiceSamples.ToListAsync());
    }

    [Fact]
    public async Task TheRuleIsAboutAutomaticMatches_NotAboutPeople()
    {
        // A user confirming a live speaker by hand DOES enrol, through ISpeakerAssignment - that is the
        // whole design, since only someone who was in the meeting can answer who a voice is. This test
        // exists so the guard above is never mistaken for "live recordings can never train a voiceprint",
        // which would remove the one source of ground truth the platform has.
        using var db = TestDb.Create();
        var (rec, person) = await SeedLiveWithEnrolledPerson(db);
        var (controller, identifier) = Build(db);
        identifier.Nearest(person.Id, "Ada", 0.95);      // no automatic match
        await controller.LiveChunk(Chunk(rec, 0));

        var speaker = await db.Speakers.SingleAsync(s => s.RecordingId == rec.Id);
        speaker.Embedding = new Pgvector.Vector(At(0));
        var assignment = new SpeakerAssignment(db, new PeopleDirectory(db));
        await assignment.AssignAsync(speaker, person);
        await db.SaveChangesAsync();

        Assert.NotEmpty(await db.VoiceSamples.Where(v => v.PersonId == person.Id).ToListAsync());
    }
}
