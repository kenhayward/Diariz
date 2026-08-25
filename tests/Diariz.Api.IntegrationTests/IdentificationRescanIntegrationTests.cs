using Diariz.Api.Configuration;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Diariz.Api.IntegrationTests;

/// <summary>Re-running identification over speakers that were transcribed before the people they belong to
/// were enrolled.
///
/// <para>Identification runs exactly once, in the transcription callback, so enrolling someone today never
/// revisits yesterday's recordings. On the measured instance that left 38 speakers sitting inside the
/// acceptance threshold, anonymous and unlinked. This is what collects them.</para>
///
/// <para>Integration rather than unit: the whole point is real pgvector ranking across many speakers, and the
/// in-memory provider ignores the vector column entirely.</para></summary>
[Collection(IntegrationCollection.Name)]
public class IdentificationRescanIntegrationTests(ContainersFixture fx)
{
    private static Vector Vec(int index, float value = 1f)
    {
        var a = new float[192];
        a[index] = value;
        return a is var arr ? new Vector(arr) : throw new InvalidOperationException();
    }

    /// <summary>A vector at a chosen cosine distance from <see cref="Vec"/>(0): mixing in an orthogonal
    /// component moves it away by a predictable amount.</summary>
    private static Vector At(double distance)
    {
        var cos = 1 - distance;
        var a = new float[192];
        a[0] = (float)cos;
        a[1] = (float)Math.Sqrt(Math.Max(0, 1 - (cos * cos)));
        return new Vector(a);
    }

    private static IdentificationRescan Rescan(DiarizDbContext db, PlatformSettings? settings = null) =>
        new(db,
            new SpeakerIdentifier(db, Options.Create(new IdentificationOptions { Enabled = true })),
            new FixedPlatformSettings(db, settings));

    /// <summary>Clears the shared directory, then seeds one person plus one anonymous speaker per requested
    /// distance. Returns the person and the speakers in the order the distances were given.</summary>
    private async Task<(Guid personId, List<Guid> speakerIds, Guid transcriptionId)> SeedAsync(
        params double[] distances)
    {
        await using var db = fx.CreateDbContext();
        db.People.RemoveRange(await db.People.ToListAsync());
        db.Speakers.RemoveRange(await db.Speakers.ToListAsync());
        await db.SaveChangesAsync();

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test" };
        var person = new Person { Id = Guid.NewGuid(), Name = "Alice", Embedding = Vec(0), SampleCount = 1 };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k" };
        var tr = new Transcription { Id = Guid.NewGuid(), RecordingId = rec.Id, Version = 1 };
        db.AddRange(user, person, rec, tr);

        var ids = new List<Guid>();
        for (var i = 0; i < distances.Length; i++)
        {
            var label = $"SPEAKER_{i:00}";
            var speaker = new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Label = label, DisplayName = label,
                Embedding = At(distances[i]),
            };
            db.Speakers.Add(speaker);
            // Plenty of speech, so the minimum-speech gate never decides one of these tests.
            db.Segments.Add(new Segment
            {
                Id = Guid.NewGuid(), TranscriptionId = tr.Id, SpeakerLabel = label,
                StartMs = i * 60_000, EndMs = (i * 60_000) + 30_000, Original = "hello", Ordinal = i,
            });
            ids.Add(speaker.Id);
        }

        await db.SaveChangesAsync();
        return (person.Id, ids, tr.Id);
    }

    [Fact]
    public async Task Applies_a_match_that_already_qualified()
    {
        var (personId, ids, _) = await SeedAsync(0.10);

        await using var db = fx.CreateDbContext();
        var report = await Rescan(db).RunAsync(dryRun: false);

        Assert.Equal(1, report.Applied);
        Assert.Equal(personId, (await db.Speakers.SingleAsync(s => s.Id == ids[0])).PersonId);
    }

    [Fact]
    public async Task Queues_a_borderline_match_instead_of_applying_it()
    {
        var (personId, ids, _) = await SeedAsync(0.35);

        await using var db = fx.CreateDbContext();
        var report = await Rescan(db).RunAsync(dryRun: false);

        Assert.Equal(0, report.Applied);
        Assert.Equal(1, report.Suggested);
        var sp = await db.Speakers.SingleAsync(s => s.Id == ids[0]);
        Assert.Equal(personId, sp.SuggestedPersonId);
        Assert.Null(sp.PersonId);
    }

    [Fact]
    public async Task A_dry_run_reports_the_same_counts_and_writes_nothing()
    {
        // The point of the preview: state "this would apply 38 and queue 90" before committing to it.
        var (_, ids, _) = await SeedAsync(0.10, 0.35, 0.90);

        await using (var db = fx.CreateDbContext())
        {
            var report = await Rescan(db).RunAsync(dryRun: true);
            Assert.Equal(1, report.Applied);
            Assert.Equal(1, report.Suggested);
            Assert.Equal(3, report.Scanned);
        }

        await using var read = fx.CreateDbContext();
        foreach (var id in ids)
        {
            var sp = await read.Speakers.SingleAsync(s => s.Id == id);
            Assert.Null(sp.PersonId);
            Assert.Null(sp.SuggestedPersonId);
        }
    }

    [Fact]
    public async Task Never_touches_a_speaker_someone_named_by_hand()
    {
        var (_, ids, _) = await SeedAsync(0.10);
        await using (var db = fx.CreateDbContext())
        {
            var sp = await db.Speakers.SingleAsync(s => s.Id == ids[0]);
            sp.DisplayName = "Bob";
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        var report = await Rescan(db2).RunAsync(dryRun: false);

        Assert.Equal(0, report.Scanned);
        Assert.Equal("Bob", (await db2.Speakers.SingleAsync(s => s.Id == ids[0])).DisplayName);
    }

    [Fact]
    public async Task Never_revokes_an_existing_automatic_label()
    {
        // A knob change must not mass-unlabel history. Automatic association is almost always right when it
        // fires, so stripping correct labels because a slider moved is strictly worse than leaving them -
        // and revoking a stale one stays where it belongs, at transcription time.
        var (personId, ids, _) = await SeedAsync(0.90);
        await using (var db = fx.CreateDbContext())
        {
            var sp = await db.Speakers.SingleAsync(s => s.Id == ids[0]);
            sp.PersonId = personId;
            sp.DisplayName = "Alice";
            sp.IdentifiedAuto = true;
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        var report = await Rescan(db2).RunAsync(dryRun: false);

        Assert.Equal(0, report.Scanned);
        var after = await db2.Speakers.SingleAsync(s => s.Id == ids[0]);
        Assert.Equal(personId, after.PersonId);
        Assert.True(after.IdentifiedAuto);
    }

    [Fact]
    public async Task Never_re_suggests_a_pair_that_was_rejected()
    {
        var (personId, ids, _) = await SeedAsync(0.35);
        await using (var db = fx.CreateDbContext())
        {
            db.SpeakerIdentityDecisions.Add(new SpeakerIdentityDecision
            {
                Id = Guid.NewGuid(), SpeakerId = ids[0], PersonId = personId,
                Decision = IdentityDecisionKind.Rejected, Distance = 0.35,
            });
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        var report = await Rescan(db2).RunAsync(dryRun: false);

        Assert.Equal(0, report.Suggested);
        Assert.Null((await db2.Speakers.SingleAsync(s => s.Id == ids[0])).SuggestedPersonId);
    }

    [Fact]
    public async Task Skips_a_speaker_with_too_little_speech()
    {
        var (_, ids, trId) = await SeedAsync(0.10);
        await using (var db = fx.CreateDbContext())
        {
            // Scoped to this seed's transcription: the shared container keeps segments from other tests, and
            // diarization labels repeat across recordings by design.
            var seg = await db.Segments.SingleAsync(
                s => s.TranscriptionId == trId && s.SpeakerLabel == "SPEAKER_00");
            seg.EndMs = seg.StartMs + 1_000;
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        var report = await Rescan(db2).RunAsync(dryRun: false);

        Assert.Equal(0, report.Applied);
        Assert.Null((await db2.Speakers.SingleAsync(s => s.Id == ids[0])).PersonId);
    }
}
