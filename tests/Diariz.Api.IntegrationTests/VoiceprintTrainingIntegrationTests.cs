using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;

namespace Diariz.Api.IntegrationTests;

/// <summary>That every surface deciding what a voiceprint is made of gives the same answer.
///
/// <para>Integration rather than unit, because all of it turns on a real <c>vector(192)</c> column - the
/// in-memory provider Ignores the embedding entirely, so a unit test here would assert against nulls and
/// pass whatever the rule said.</para>
///
/// <para>The defect: both assignment paths move a speaker's link and leave its <see cref="VoiceSample"/>
/// behind. On the instance this was written for, six samples were training a person the transcript no longer
/// named - three of them a specifically different person.</para></summary>
[Collection(IntegrationCollection.Name)]
public class VoiceprintTrainingIntegrationTests(ContainersFixture fx)
{
    /// <summary>A unit vector along one axis. Two of them are orthogonal, so a centroid built from the wrong
    /// pair is unmistakably different from one built from the right pair - where two vectors merely "far
    /// apart" would leave the assertion arguing about a decimal place.</summary>
    private static Vector Axis(int i)
    {
        var v = new float[192];
        v[i] = 1f;
        return new Vector(v);
    }

    private static PeopleController Controller(DiarizDbContext db, Guid userId) =>
        new(db, new RoomScope(db), new PeopleDirectory(db), new UserPermissions(db), new FakeJobQueue(),
            new FakeAudioClipper(), new FakeAudioStorage(), NullLogger<PeopleController>.Instance,
            new PlatformSettingsService(db))
        {
            ControllerContext = Http.Context(userId),
        };

    /// <summary>A person with two orthogonal samples in one recording. The second speaker's link is decided
    /// by the caller from the person's own id, so each test varies exactly one clause of the rule: unlinked,
    /// linked elsewhere, or still linked but marked as overlapping speech.</summary>
    private async Task<(Guid personId, Guid userId)> SeedAsync(
        Func<Guid, Guid?> secondSpeakerLink, bool multi = false)
    {
        await using var db = fx.CreateDbContext();

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test" };
        var person = new Person { Id = Guid.NewGuid(), Name = "Ada" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, Title = "Standup", BlobKey = "k" };
        db.AddRange(user, person, rec);

        var kept = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Ada",
            PersonId = person.Id, Embedding = Axis(0),
        };
        // Enrolled as this person, then moved. Exactly what Unassign and AssignAsync leave behind: the
        // speaker's link changes, the sample does not.
        var moved = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "SPEAKER_01",
            PersonId = secondSpeakerLink(person.Id), IsMultiSpeaker = multi, Embedding = Axis(1),
        };
        db.AddRange(kept, moved);

        db.AddRange(
            new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = kept.Id, RecordingId = rec.Id,
                Embedding = Axis(0),
            },
            new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = moved.Id, RecordingId = rec.Id,
                Embedding = Axis(1),
            });

        Perms.Grant(db, user.Id, PlatformPermission.ManagePeople);
        await db.SaveChangesAsync();
        return (person.Id, user.Id);
    }

    /// <summary>Asserts the centroid is the first axis alone. Both samples would put roughly 0.707 on each of
    /// the two axes, so this cannot pass by accident.</summary>
    private static void AssertOnlyTheLinkedSampleCounted(Person person)
    {
        Assert.NotNull(person.Embedding);
        var centroid = person.Embedding!.ToArray();
        Assert.True(centroid[0] > 0.99, $"expected the linked sample's axis, got {centroid[0]}");
        Assert.True(centroid[1] < 0.01, $"expected nothing from the moved sample, got {centroid[1]}");

        // The figure the UI shows is derived from the same list, or the count and the audio behind the
        // voiceprint disagree.
        Assert.Equal(1, person.SampleCount);
    }

    [Fact]
    public async Task An_unlinked_speakers_sample_is_not_in_the_centroid()
    {
        var (personId, _) = await SeedAsync(_ => null);

        await using var db = fx.CreateDbContext();
        await new PeopleDirectory(db).RecomputeVoiceprintAsync(personId);

        AssertOnlyTheLinkedSampleCounted((await db.People.FindAsync(personId))!);
    }

    [Fact]
    public async Task A_speaker_reassigned_to_someone_else_is_not_in_the_centroid()
    {
        // The worst of the six: person A's voiceprint still learning from audio since labelled as person B,
        // so both of them are taught the same voice and neither can be told apart from it.
        await using var setup = fx.CreateDbContext();
        var other = new Person { Id = Guid.NewGuid(), Name = "Grace" };
        setup.People.Add(other);
        await setup.SaveChangesAsync();

        var (personId, _) = await SeedAsync(_ => other.Id);

        await using var db = fx.CreateDbContext();
        await new PeopleDirectory(db).RecomputeVoiceprintAsync(personId);

        AssertOnlyTheLinkedSampleCounted((await db.People.FindAsync(personId))!);
    }

    [Fact]
    public async Task Overlapping_speech_is_not_in_the_centroid()
    {
        // A speaker marked as multiple people is a mix of voices. It was already kept out of the Voiceprint
        // tab's list, which is precisely how one stayed inside a centroid unnoticed.
        var (personId, _) = await SeedAsync(self => self, multi: true);

        await using var db = fx.CreateDbContext();
        await new PeopleDirectory(db).RecomputeVoiceprintAsync(personId);

        AssertOnlyTheLinkedSampleCounted((await db.People.FindAsync(personId))!);
    }

    [Fact]
    public async Task Diagnostics_measure_against_the_same_set_the_centroid_uses()
    {
        // The bug that made the Diagnostics tab unusable. It listed samples; the Voiceprint tab listed
        // linked speakers. A sample whose speaker had been unlinked was therefore diagnosed on a screen
        // that offered no way to reach it - which is exactly what the live instance showed for the
        // top-ranked person.
        var (personId, userId) = await SeedAsync(_ => null);

        await using var db = fx.CreateDbContext();
        var result = await Controller(db, userId).Diagnostics(personId);
        var body = Assert.IsType<VoiceprintDiagnosticsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        // Both samples are still listed - hiding one is how it survived unnoticed - but only the linked
        // one trains, so there is no pair and therefore nothing that can be an outlier.
        Assert.Equal(2, body.Samples.Count);
        Assert.Equal(1, body.Samples.Count(x => x.IsTraining));
        Assert.Equal(0, body.AloneCount);
        Assert.Null(body.WidestPair);
    }

    [Fact]
    public async Task The_health_ranking_measures_against_the_same_set_too()
    {
        // One training sample is not a training set. Ranking this person as scattered would send someone to
        // review a problem that does not exist, which is the failure mode a ranking cannot afford.
        var (moved, userId) = await SeedAsync(_ => null);

        // A genuinely scattered control, seeded identically except that both speakers stay linked. Without
        // it an empty ranking would satisfy the assertion below for entirely the wrong reason - the endpoint
        // returns early when nobody qualifies.
        var (scattered, _) = await SeedAsync(self => self);

        await using var db = fx.CreateDbContext();
        var result = await Controller(db, userId).DirectoryDiagnostics();
        var rows = Assert.IsAssignableFrom<IReadOnlyList<PersonDiagnosticsSummaryDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Contains(rows, r => r.PersonId == scattered);
        Assert.DoesNotContain(rows, r => r.PersonId == moved);
    }

    [Fact]
    public async Task A_moved_speakers_sample_is_still_listed_and_says_so()
    {
        // The counterpart to keeping it out of the centroid. Dropping six samples out of six voiceprints
        // with no trace would repeat the original defect in a new place: invisible is how they survived.
        var (personId, userId) = await SeedAsync(_ => null);

        await using var db = fx.CreateDbContext();
        var result = await Controller(db, userId).Attributions(personId);
        var rows = Assert.IsAssignableFrom<IReadOnlyList<PersonAttributionDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(2, rows.Count);

        var moved = rows.Single(r => r.SpeakerLabel == "SPEAKER_01");
        Assert.False(moved.StillLinked);
        Assert.False(moved.IsTraining);
        // The sample is named, not hidden: that is what makes the row explain itself rather than just
        // appear.
        Assert.NotNull(moved.VoiceSampleId);

        var kept = rows.Single(r => r.SpeakerLabel == "SPEAKER_00");
        Assert.True(kept.StillLinked);
        Assert.True(kept.IsTraining);
    }

    [Fact]
    public async Task Assessment_access_does_not_carry_the_right_to_reassign()
    {
        // Manage voiceprints exists so someone can listen to a segment and judge whether it is the right
        // person. Editing the owner's transcript is a different act, and AssignSpeaker refuses it - so the
        // control must not be offered, or it is a button that always fails.
        var (personId, ownerId) = await SeedAsync(self => self);

        await using var setup = fx.CreateDbContext();
        var assessor = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test" };
        setup.Users.Add(assessor);
        Perms.Grant(setup, assessor.Id, PlatformPermission.ManagePeople | PlatformPermission.ManageVoiceprints);
        await setup.SaveChangesAsync();

        await using var db = fx.CreateDbContext();
        var mine = Assert.IsAssignableFrom<IReadOnlyList<PersonAttributionDto>>(
            Assert.IsType<OkObjectResult>((await Controller(db, ownerId).Attributions(personId)).Result).Value);
        var theirs = Assert.IsAssignableFrom<IReadOnlyList<PersonAttributionDto>>(
            Assert.IsType<OkObjectResult>((await Controller(db, assessor.Id).Attributions(personId)).Result).Value);

        Assert.All(mine, r => Assert.True(r.CanReassign));

        // Listed and audible to the assessor, but not editable - the two permissions are deliberately not
        // the same permission.
        Assert.All(theirs, r => Assert.True(r.CanAccessRecording));
        Assert.All(theirs, r => Assert.False(r.CanReassign));
    }

    /// <summary>The centroid a person's row actually held before the rule existed: the mean of both voices,
    /// normalised. Set by hand because the only code that can produce it is the code being fixed.</summary>
    private static Vector BothVoices()
    {
        var v = new float[192];
        v[0] = 0.70710678f;
        v[1] = 0.70710678f;
        return new Vector(v);
    }

    [Fact]
    public async Task The_startup_pass_rebuilds_a_centroid_built_from_a_sample_that_no_longer_counts()
    {
        // The rule fixes what is computed from now on. It does not touch the six centroids already stored,
        // which stay wrong until something recomputes them - and nothing would, until the next time someone
        // happened to edit that person.
        var (personId, _) = await SeedAsync(_ => null);

        await using (var stale = fx.CreateDbContext())
        {
            var p = await stale.People.FindAsync(personId);
            p!.Embedding = BothVoices();
            p.SampleCount = 2;
            await stale.SaveChangesAsync();
        }

        await using var db = fx.CreateDbContext();
        var rebuilt = await VoiceprintRebuild.RunAsync(db, new PeopleDirectory(db), NullLogger.Instance);

        // Other tests in this shared-container collection seed orphans too, so the count is a lower bound.
        // What is asserted exactly is this person's centroid.
        Assert.True(rebuilt >= 1, $"expected at least this person to be rebuilt, got {rebuilt}");
        AssertOnlyTheLinkedSampleCounted((await db.People.FindAsync(personId))!);
    }

    [Fact]
    public async Task A_second_pass_changes_nothing()
    {
        // The property that lets it run on every boot. It is idempotent in effect rather than in selection:
        // the orphan row still exists afterwards, so the person is still found - and recomputing a converged
        // centroid from the one derivation that produced it must be a no-op.
        var (personId, _) = await SeedAsync(_ => null);

        await using var db = fx.CreateDbContext();
        var people = new PeopleDirectory(db);

        await VoiceprintRebuild.RunAsync(db, people, NullLogger.Instance);
        var first = (await db.People.FindAsync(personId))!.Embedding!.ToArray();

        await VoiceprintRebuild.RunAsync(db, people, NullLogger.Instance);
        var second = (await db.People.FindAsync(personId))!.Embedding!.ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task The_startup_pass_leaves_a_healthy_voiceprint_alone()
    {
        // It must not become "recompute the whole directory on every boot". Only a person actually holding a
        // sample the rule rejects is touched.
        var (healthy, _) = await SeedAsync(self => self);

        // A deliberately wrong centroid on a person the rule has no complaint about. If the pass recomputed
        // everyone it would correct this, and the assertion below would fail - which is the whole point.
        await using (var tamper = fx.CreateDbContext())
        {
            var p = await tamper.People.FindAsync(healthy);
            p!.Embedding = Axis(7);
            await tamper.SaveChangesAsync();
        }

        await using var db = fx.CreateDbContext();
        await VoiceprintRebuild.RunAsync(db, new PeopleDirectory(db), NullLogger.Instance);

        await using var read = fx.CreateDbContext();
        var after = (await read.People.FindAsync(healthy))!.Embedding!.ToArray();
        Assert.Equal(Axis(7).ToArray(), after);
    }
}
