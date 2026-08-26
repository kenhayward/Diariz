using Diariz.Api.Contracts;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Diariz.Api.IntegrationTests;

/// <summary>Diagnosing a person's training set against real Postgres.
///
/// <para>Integration rather than unit: the embeddings are <c>vector(192)</c>, which the in-memory provider
/// Ignores. A unit test would diagnose a set of nulls and prove nothing about the thing being built.</para></summary>
[Collection(IntegrationCollection.Name)]
public class VoiceprintDiagnosticsIntegrationTests(ContainersFixture fx) : IAsyncLifetime
{
    /// <summary>Every test in this class starts from an empty directory.
    ///
    /// <para>Structural rather than per-test, because the diagnosis became <b>directory-wide</b> the moment
    /// it started asking "is somebody else closer?". Every verdict is now a claim about every other person
    /// in the table, including whatever another class in this shared-container collection seeded - so a test
    /// without this passes or fails on what ran before it. Three did, and others were passing by luck.</para></summary>
    public Task InitializeAsync() => ClearDirectoryAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A unit vector at a chosen cosine distance from <see cref="At"/>(0).</summary>
    private static Vector At(double distance)
    {
        var cos = 1 - distance;
        var v = new float[192];
        v[0] = (float)cos;
        v[1] = (float)Math.Sqrt(Math.Max(0, 1 - (cos * cos)));
        return new Vector(v);
    }

    private static PeopleController Build(DiarizDbContext db, Guid userId) =>
        new(db, new RoomScope(db), new PeopleDirectory(db), new UserPermissions(db), new FakeJobQueue(),
            new FakeAudioClipper(), new FakeAudioStorage(), NullLogger<PeopleController>.Instance, new PlatformSettingsService(db))
        {
            ControllerContext = Http.Context(userId),
        };

    /// <summary>A person with one sample per requested distance, each in its own recording.</summary>
    private async Task<(Guid personId, List<Guid> sampleIds, Guid userId)> SeedAsync(
        params double[] distances)
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test" };
        var person = new Person { Id = Guid.NewGuid(), Name = "Alice", SampleCount = distances.Length };
        db.AddRange(user, person);

        var ids = new List<Guid>();
        for (var i = 0; i < distances.Length; i++)
        {
            var rec = new Recording
            {
                Id = Guid.NewGuid(), UserId = user.Id, Title = $"Meeting {i}", BlobKey = "k",
            };
            var speaker = new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Alice",
                PersonId = person.Id, Embedding = At(distances[i]),
            };
            var sample = new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = speaker.Id, RecordingId = rec.Id,
                Embedding = At(distances[i]),
            };
            db.AddRange(rec, speaker, sample);
            ids.Add(sample.Id);
        }

        Perms.Grant(db, user.Id, PlatformPermission.ManagePeople);
        await db.SaveChangesAsync();
        return (person.Id, ids, user.Id);
    }

    /// <summary>A person whose samples are mutually orthogonal - each in its own dimension, so every one is
    /// distant from every other.
    ///
    /// <para>Distinct from <see cref="SeedAsync"/> on purpose. Samples placed at 0.9, 0.95 and 1.0 from one
    /// reference vector all sit on the same arc and are therefore close to <em>each other</em>: they form a
    /// cluster, not three outliers. Being far from a common point is not the same as being far apart.</para></summary>
    private async Task<(Guid personId, Guid userId)> SeedOrthogonalAsync(int count)
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test" };
        var person = new Person { Id = Guid.NewGuid(), Name = "Scattered", SampleCount = count };
        db.AddRange(user, person);

        for (var i = 0; i < count; i++)
        {
            var axis = new float[192];
            axis[i] = 1f;
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, Title = $"M{i}", BlobKey = "k" };
            var speaker = new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Scattered",
                PersonId = person.Id, Embedding = new Vector(axis),
            };
            db.AddRange(rec, speaker, new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = speaker.Id, RecordingId = rec.Id,
                Embedding = new Vector(axis),
            });
        }

        Perms.Grant(db, user.Id, PlatformPermission.ManagePeople);
        await db.SaveChangesAsync();
        return (person.Id, user.Id);
    }


    /// <summary>Two people on the same arc: one with a tight pair of its own, and one whose second sample
    /// sits closer to the other person than to its own sibling.</summary>
    private async Task<(Guid subject, Guid other, string otherName, Guid oddSample, Guid userId)>
        SeedImpostorAsync(double siblingAt, double impostorAt)
    {
        await using var db = fx.CreateDbContext();

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test" };
        var subject = new Person { Id = Guid.NewGuid(), Name = "Ada", SampleCount = 2 };
        var other = new Person { Id = Guid.NewGuid(), Name = "Grace", SampleCount = 1 };
        db.AddRange(user, subject, other);

        Guid Add(Person p, double at)
        {
            var rec = new Recording
            {
                Id = Guid.NewGuid(), UserId = user.Id, Title = $"M{at}", BlobKey = "k",
            };
            var speaker = new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = p.Name,
                PersonId = p.Id, Embedding = At(at),
            };
            var sample = new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = p.Id, SpeakerId = speaker.Id, RecordingId = rec.Id,
                Embedding = At(at),
            };
            db.AddRange(rec, speaker, sample);
            return sample.Id;
        }

        Add(subject, 0);                       // the anchor
        var odd = Add(subject, siblingAt);     // this one is diagnosed
        Add(other, impostorAt);                // somebody else, placed relative to the anchor

        Perms.Grant(db, user.Id, PlatformPermission.ManagePeople);
        await db.SaveChangesAsync();
        return (subject.Id, other.Id, other.Name, odd, user.Id);
    }

    private static VoiceprintDiagnosticsDto Body(ActionResult<VoiceprintDiagnosticsDto> r) =>
        Assert.IsType<VoiceprintDiagnosticsDto>(Assert.IsType<OkObjectResult>(r.Result).Value);

    [Fact]
    public async Task A_tight_training_set_reports_no_outliers()
    {
        var (personId, _, userId) = await SeedAsync(0, 0.05, 0.1);

        await using var db = fx.CreateDbContext();
        var body = Body(await Build(db, userId).Diagnostics(personId));

        Assert.Equal(0, body.AloneCount);
        Assert.All(body.Samples, s => Assert.Equal(nameof(SampleVerdict.Core), s.Verdict));
    }

    [Fact]
    public async Task A_distant_sample_is_reported_alone_and_named()
    {
        // The whole point: which recording to go and listen to.
        var (personId, ids, userId) = await SeedAsync(0, 0.05, 0.9);

        await using var db = fx.CreateDbContext();
        var body = Body(await Build(db, userId).Diagnostics(personId));

        Assert.Equal(1, body.AloneCount);
        var lone = body.Samples.Single(s => s.VoiceSampleId == ids[2]);
        Assert.Equal(nameof(SampleVerdict.Alone), lone.Verdict);
        Assert.Equal("Meeting 2", lone.RecordingName);
        Assert.Equal("SPEAKER_00", lone.SpeakerLabel);
    }

    [Fact]
    public async Task A_single_sample_is_not_an_outlier()
    {
        // Most of the directory is in this state. Reporting it as a problem would bury the real ones.
        var (personId, _, userId) = await SeedAsync(0);

        await using var db = fx.CreateDbContext();
        var body = Body(await Build(db, userId).Diagnostics(personId));

        Assert.Equal(0, body.AloneCount);
        Assert.Equal(nameof(SampleVerdict.Only), Assert.Single(body.Samples).Verdict);
        Assert.Null(body.WidestPair);
    }

    [Fact]
    public async Task The_widest_pair_is_reported()
    {
        // One number for "how scattered is this person", so the directory ranking has something to sort on.
        var (personId, _, userId) = await SeedAsync(0, 0.05, 0.9);

        await using var db = fx.CreateDbContext();
        var body = Body(await Build(db, userId).Diagnostics(personId));

        Assert.NotNull(body.WidestPair);
        Assert.Equal(0.9, body.WidestPair!.Value, 1);
    }

    [Fact]
    public async Task An_excluded_sample_is_diagnosed_but_marked()
    {
        // Not hidden: seeing that the sample you already dropped was the outlier is the confirmation that
        // dropping it was right.
        var (personId, ids, userId) = await SeedAsync(0, 0.05, 0.9);
        await using (var db = fx.CreateDbContext())
        {
            (await db.VoiceSamples.SingleAsync(v => v.Id == ids[2])).ExcludedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        var body = Body(await Build(db2, userId).Diagnostics(personId));

        Assert.Equal(3, body.Samples.Count);
        Assert.False(body.Samples.Single(s => s.VoiceSampleId == ids[2]).IsTraining);
        Assert.All(
            body.Samples.Where(s => s.VoiceSampleId != ids[2]),
            s => Assert.True(s.IsTraining));
    }

    [Fact]
    public async Task An_excluded_sample_does_not_drag_the_diagnosis_of_the_others()
    {
        // It is not training the voiceprint, so it must not define what the others are measured against -
        // otherwise dropping the outlier would make everything else look like an outlier instead.
        var (personId, ids, userId) = await SeedAsync(0, 0.05, 0.9);
        await using (var db = fx.CreateDbContext())
        {
            (await db.VoiceSamples.SingleAsync(v => v.Id == ids[2])).ExcludedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        var body = Body(await Build(db2, userId).Diagnostics(personId));

        Assert.Equal(0, body.AloneCount);
        Assert.All(
            body.Samples.Where(s => s.IsTraining),
            s => Assert.Equal(nameof(SampleVerdict.Core), s.Verdict));
    }

    [Fact]
    public async Task Diagnostics_without_ManagePeople_is_forbidden()
    {
        var (personId, _, _) = await SeedAsync(0, 0.1);

        await using var db = fx.CreateDbContext();
        Assert.IsType<ForbidResult>((await Build(db, Guid.NewGuid()).Diagnostics(personId)).Result);
    }

    [Fact]
    public async Task Diagnostics_for_an_unknown_person_is_not_found()
    {
        var (_, _, userId) = await SeedAsync(0);

        await using var db = fx.CreateDbContext();
        Assert.IsType<NotFoundResult>((await Build(db, userId).Diagnostics(Guid.NewGuid())).Result);
    }

    // ---- The directory ranking ----

    /// <summary>Clears the shared directory so a ranking assertion is about this test's people only - the
    /// endpoint deliberately spans the whole platform, so anything another test seeded would show up in it.</summary>
    private async Task ClearDirectoryAsync()
    {
        await using var db = fx.CreateDbContext();
        db.People.RemoveRange(await db.People.ToListAsync());
        await db.SaveChangesAsync();
    }

    private static IReadOnlyList<PersonDiagnosticsSummaryDto> Ranking(
        ActionResult<IReadOnlyList<PersonDiagnosticsSummaryDto>> r) =>
        Assert.IsAssignableFrom<IReadOnlyList<PersonDiagnosticsSummaryDto>>(
            Assert.IsType<OkObjectResult>(r.Result).Value);

    [Fact]
    public async Task The_ranking_omits_a_healthy_person()
    {
        // A list that includes healthy people is worse than useless: the ones with a real problem stop
        // standing out, which is the entire job of a ranking.
        var (_, _, userId) = await SeedAsync(0, 0.05, 0.1);

        await using var db = fx.CreateDbContext();
        Assert.Empty(Ranking(await Build(db, userId).DirectoryDiagnostics()));
    }

    [Fact]
    public async Task The_ranking_omits_a_person_with_a_single_sample()
    {
        // Nothing wrong, only nothing to say. Most of the directory is in this state.
        var (_, _, userId) = await SeedAsync(0);

        await using var db = fx.CreateDbContext();
        Assert.Empty(Ranking(await Build(db, userId).DirectoryDiagnostics()));
    }

    [Fact]
    public async Task The_ranking_lists_a_person_with_an_outlier()
    {
        var (personId, _, userId) = await SeedAsync(0, 0.05, 0.9);

        await using var db = fx.CreateDbContext();
        var row = Assert.Single(Ranking(await Build(db, userId).DirectoryDiagnostics()));

        Assert.Equal(personId, row.PersonId);
        Assert.Equal("Alice", row.Name);
        Assert.Equal(1, row.AloneCount);
        Assert.Equal(3, row.SampleCount);
    }

    [Fact]
    public async Task The_ranking_puts_the_worst_first()
    {
        var (mild, _, userId) = await SeedAsync(0, 0.05, 0.9);
        var (bad, _) = await SeedOrthogonalAsync(4);

        await using var db = fx.CreateDbContext();
        var rows = Ranking(await Build(db, userId).DirectoryDiagnostics());

        Assert.Equal(2, rows.Count);
        Assert.Equal(bad, rows[0].PersonId);
        Assert.Equal(mild, rows[1].PersonId);
        Assert.True(rows[0].AloneCount > rows[1].AloneCount);
    }

    [Fact]
    public async Task The_ranking_ignores_an_excluded_sample()
    {
        // Dropping the outlier is the fix. If the ranking kept counting it, the list would never empty and
        // acting on it would look like it had done nothing.
        var (_, ids, userId) = await SeedAsync(0, 0.05, 0.9);
        await using (var db = fx.CreateDbContext())
        {
            (await db.VoiceSamples.SingleAsync(v => v.Id == ids[2])).ExcludedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        Assert.Empty(Ranking(await Build(db2, userId).DirectoryDiagnostics()));
    }

    [Fact]
    public async Task The_ranking_without_ManagePeople_is_forbidden()
    {
        await SeedAsync(0, 0.9);

        await using var db = fx.CreateDbContext();
        Assert.IsType<ForbidResult>((await Build(db, Guid.NewGuid()).DirectoryDiagnostics()).Result);
    }

    // ---- Is somebody else closer? Measured live, 27 of 92 samples belonging to multi-sample people were,
    // and 9 of those sat within the accept distance of that other person. ----

    [Fact]
    public async Task A_sample_closer_to_someone_else_names_who()
    {
        // The name is the point: it turns a diagnosis into a reassignment, and the control for that is
        // already on the row. A bare distance would leave the user to work out who from scratch.
        var (personId, otherId, otherName, odd, userId) = await SeedImpostorAsync(siblingAt: 0.9, impostorAt: 0.95);

        await using var db = fx.CreateDbContext();
        var body = Body(await Build(db, userId).Diagnostics(personId));

        var row = body.Samples.Single(x => x.VoiceSampleId == odd);
        Assert.Equal(nameof(SampleVerdict.Impostor), row.Verdict);
        Assert.Equal(otherId, row.NearestImpostorPersonId);
        Assert.Equal(otherName, row.NearestImpostorName);
    }

    [Fact]
    public async Task A_sample_that_looks_healthy_is_caught_when_someone_else_is_closer()
    {
        // Sits inside the accept distance of its own sibling - rated "matches the others" on the sibling
        // question alone - while somebody else sits closer still. Exactly one live sample was in this
        // state, and no sibling-only verdict could ever reach it.
        var (personId, _, _, odd, userId) = await SeedImpostorAsync(siblingAt: 0.25, impostorAt: 0.10);

        await using var db = fx.CreateDbContext();
        var body = Body(await Build(db, userId).Diagnostics(personId));

        Assert.Equal(
            nameof(SampleVerdict.Impostor),
            body.Samples.Single(x => x.VoiceSampleId == odd).Verdict);
    }

    [Fact]
    public async Task The_ranking_puts_an_impostor_above_a_merely_scattered_person()
    {
        // "This is somebody else" is a different order of problem from "this sounds unlike the rest", and
        // it is the one that becomes a confident false match the moment anything clusters the set.
        var (impostorPerson, _, _, _, userId) = await SeedImpostorAsync(siblingAt: 0.9, impostorAt: 0.95);

        // A second person who is merely scattered: two mutually distant samples, far from everyone.
        await using (var db = fx.CreateDbContext())
        {
            var scattered = new Person { Id = Guid.NewGuid(), Name = "Alice", SampleCount = 2 };
            db.People.Add(scattered);
            foreach (var axis in new[] { 100, 150 })
            {
                var v = new float[192];
                v[axis] = 1f;
                var rec = new Recording
                {
                    Id = Guid.NewGuid(), UserId = userId, Title = $"S{axis}", BlobKey = "k",
                };
                var sp = new Speaker
                {
                    Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
                    DisplayName = "Alice", PersonId = scattered.Id, Embedding = new Vector(v),
                };
                db.AddRange(rec, sp, new VoiceSample
                {
                    Id = Guid.NewGuid(), PersonId = scattered.Id, SpeakerId = sp.Id,
                    RecordingId = rec.Id, Embedding = new Vector(v),
                });
            }
            await db.SaveChangesAsync();
        }

        await using var read = fx.CreateDbContext();
        var result = await Build(read, userId).DirectoryDiagnostics();
        var rows = Assert.IsAssignableFrom<IReadOnlyList<PersonDiagnosticsSummaryDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(impostorPerson, rows[0].PersonId);
        Assert.True(rows[0].ImpostorCount > 0, "the person with an impostor leads the ranking");
    }

    [Fact]
    public async Task A_healthy_person_reports_no_impostor_and_stays_out_of_the_ranking()
    {
        // The negative case, and the one most at risk of a false alarm: a tight training set in a directory
        // that also contains somebody else. Two people sounding a bit alike is not a finding.
        var (personId, _, _, _, userId) = await SeedImpostorAsync(siblingAt: 0.05, impostorAt: 0.9);

        await using var db = fx.CreateDbContext();
        var body = Body(await Build(db, userId).Diagnostics(personId));
        Assert.All(body.Samples, x => Assert.NotEqual(nameof(SampleVerdict.Impostor), x.Verdict));

        var result = await Build(db, userId).DirectoryDiagnostics();
        var rows = Assert.IsAssignableFrom<IReadOnlyList<PersonDiagnosticsSummaryDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.DoesNotContain(rows, r => r.PersonId == personId);
    }
}
