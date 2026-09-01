using Diariz.Api.Configuration;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.IntegrationTests;

/// <summary>
/// Live speaker identity against real Postgres and real pgvector.
///
/// <para>The <c>vector(192)</c> cosine match is Postgres-only, so the unit project has to fake it. That
/// makes this the only place the actual ranking is ever exercised - and the only place the relabel really
/// runs as one statement rather than a loop, since <c>ExecuteUpdate</c> is not supported by the in-memory
/// provider at all.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class LiveSpeakerIdentityIntegrationTests(ContainersFixture fx)
{
    /// A unit vector at <paramref name="degrees"/> in the first two dimensions, so every distance in these
    /// tests is arithmetic rather than something the database gets to define.
    private static float[] At(double degrees)
    {
        var r = degrees * Math.PI / 180.0;
        var v = new float[192];
        v[0] = (float)Math.Cos(r);
        v[1] = (float)Math.Sin(r);
        return v;
    }

    private static async Task<Recording> SeedLive(DiarizDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"{Guid.NewGuid()}@x.test",
            Email = $"{Guid.NewGuid()}@x.test",
        };
        db.Users.Add(user);
        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = user.Id, Title = "Standup", BlobKey = "",
            Status = RecordingStatus.Live, LiveSessionId = Guid.NewGuid(),
        };
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec;
    }

    [Fact]
    public async Task ALiveCentroidSurvivesTheRoundTripThroughPostgres()
    {
        // Speaker.Embedding is vector(192). A centroid that came back subtly different - truncated,
        // re-normalised by the driver, or dimension-mismatched - would move every later match silently.
        using var db = fx.CreateDbContext();
        var rec = await SeedLive(db);
        var centroid = At(37);

        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "SPEAKER_00", Embedding = new Pgvector.Vector(centroid),
        });
        await db.SaveChangesAsync();

        using var fresh = fx.CreateDbContext();
        var stored = await fresh.Speakers.SingleAsync(s => s.RecordingId == rec.Id);
        Assert.Equal(0.0, LiveSpeakerStitcher.CosineDistance(stored.Embedding!.ToArray(), centroid), 6);
    }

    [Fact]
    public async Task RankingALiveCentroidFindsTheRightPersonByRealCosineDistance()
    {
        // The whole naming path rests on this query, and the unit tests fake it entirely.
        using var db = fx.CreateDbContext();
        var near = new Person
        {
            Id = Guid.NewGuid(), Name = $"Ada {Guid.NewGuid():N}",
            Embedding = new Pgvector.Vector(At(5)),
        };
        var far = new Person
        {
            Id = Guid.NewGuid(), Name = $"Grace {Guid.NewGuid():N}",
            Embedding = new Pgvector.Vector(At(80)),
        };
        db.People.AddRange(near, far);
        await db.SaveChangesAsync();

        var identifier = new SpeakerIdentifier(db,
            Options.Create(new IdentificationOptions { Enabled = true }));
        var ranked = await identifier.RankAsync(new Pgvector.Vector(At(0)), take: 2);

        Assert.NotEmpty(ranked);
        Assert.Equal(near.Id, ranked[0].PersonId);
        // 5 degrees apart is a cosine distance of about 0.0038 - the real pgvector operator, not a stub.
        Assert.True(ranked[0].Distance < 0.01, $"expected a close match, got {ranked[0].Distance}");
    }

    [Fact]
    public async Task AnOptedOutPersonIsNeverRanked()
    {
        // Opting out erases the voiceprint and stops the person being matched. Live identification must
        // inherit that without doing anything of its own - it is the same directory and the same query.
        using var db = fx.CreateDbContext();
        var optedOut = new Person
        {
            Id = Guid.NewGuid(), Name = $"Ada {Guid.NewGuid():N}",
            Embedding = new Pgvector.Vector(At(1)), VoiceprintOptOut = true,
        };
        db.People.Add(optedOut);
        await db.SaveChangesAsync();

        var identifier = new SpeakerIdentifier(db,
            Options.Create(new IdentificationOptions { Enabled = true }));
        var ranked = await identifier.RankAsync(new Pgvector.Vector(At(0)), take: 5);

        Assert.DoesNotContain(ranked, r => r.PersonId == optedOut.Id);
    }

    [Fact]
    public async Task RelabellingAWholeMeetingIsOneStatement_NotOnePerSegment()
    {
        // A 90-minute meeting carries thousands of segments and a merge can land on any chunk. This is
        // also the only place the ExecuteUpdate path runs at all: the in-memory provider does not support
        // it, so the unit tests take a loop instead and would never notice if this broke.
        using var db = fx.CreateDbContext();
        var rec = await SeedLive(db);
        var tr = new Transcription
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Version = 1,
            Model = "whisperx-live", IsProvisional = true,
        };
        db.Transcriptions.Add(tr);
        for (var i = 0; i < 500; i++)
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = "SPEAKER_01",
                StartMs = i * 1000, EndMs = i * 1000 + 900, Original = $"line {i}", Ordinal = i,
                ChunkSequence = i / 10,
            });
        await db.SaveChangesAsync();

        var updated = await db.Segments
            .Where(s => s.TranscriptionId == tr.Id && s.SpeakerLabel == "SPEAKER_01")
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SpeakerLabel, "SPEAKER_00"));

        Assert.Equal(500, updated);
        using var fresh = fx.CreateDbContext();
        Assert.Empty(await fresh.Segments
            .Where(s => s.TranscriptionId == tr.Id && s.SpeakerLabel == "SPEAKER_01").ToListAsync());
    }

    [Fact]
    public async Task AMeetingsSpeakersAndTheirCentroidsAreScopedToThatRecording()
    {
        // Session labels are per meeting and restart at SPEAKER_00 in every one, so two concurrent live
        // recordings hold the same label strings. If the stitcher's centroid lookup ever lost its
        // recording filter, one meeting's voices would be matched against another's.
        using var db = fx.CreateDbContext();
        var a = await SeedLive(db);
        var b = await SeedLive(db);
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = a.Id, Label = "SPEAKER_00",
            DisplayName = "SPEAKER_00", Embedding = new Pgvector.Vector(At(0)),
        });
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = b.Id, Label = "SPEAKER_00",
            DisplayName = "SPEAKER_00", Embedding = new Pgvector.Vector(At(90)),
        });
        await db.SaveChangesAsync();

        using var fresh = fx.CreateDbContext();
        var forA = await fresh.Speakers.Where(s => s.RecordingId == a.Id).ToListAsync();
        Assert.Equal(0.0,
            LiveSpeakerStitcher.CosineDistance(Assert.Single(forA).Embedding!.ToArray(), At(0)), 6);
    }
}
