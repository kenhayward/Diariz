using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;

namespace Diariz.Api.IntegrationTests;

/// <summary>Exercises the Postgres-only pgvector paths the in-memory unit provider can't:
/// real cosine nearest-match identification and the FK cascade/SetNull behaviour.</summary>
[Collection(IntegrationCollection.Name)]
public class SpeakerIdentificationIntegrationTests(ContainersFixture fx)
{
    private static Vector Vec(params (int index, float value)[] entries)
    {
        var a = new float[192];
        foreach (var (i, v) in entries) a[i] = v;
        return new Vector(a);
    }

    private async Task<ApplicationUser> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Empties the directory before an identification assertion.
    ///
    /// Identification is platform-wide now, so "no match" is a claim about every row in the table - including
    /// people other tests in this shared-container collection seeded. The per-user filter used to provide
    /// that isolation for free; it does not exist any more, so the isolation has to be explicit.</summary>
    private async Task ClearDirectoryAsync()
    {
        await using var db = fx.CreateDbContext();
        db.People.RemoveRange(await db.People.ToListAsync());
        await db.SaveChangesAsync();
    }

    private static SpeakerIdentifier Identifier(Diariz.Domain.DiarizDbContext db, double threshold = 0.4) =>
        new(db, Options.Create(new IdentificationOptions { Enabled = true, Threshold = threshold }));

    // Voiceprints live in their owner's personal room now; mint it so the seeded profile is in scope.
    private static Task<Guid> RoomOf(Diariz.Domain.DiarizDbContext db, Guid owner) =>
        new RoomScope(db).PersonalRoomIdAsync(owner);

    [Fact]
    public async Task VectorColumns_RoundTrip_OnProfileAndSpeaker()
    {
        var user = await SeedUser();
        var profileId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        var speakerId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.People.Add(new Person
            {
                Id = profileId, CreatedByUserId = user.Id, RoomId = await RoomOf(db, user.Id), Name = "Alice",
                Embedding = Vec((0, 1f), (5, 0.5f)), SampleCount = 1
            });
            db.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k" });
            db.Speakers.Add(new Speaker
            {
                Id = speakerId, RecordingId = recId, Label = "SPEAKER_00", DisplayName = "SPEAKER_00",
                Embedding = Vec((0, 0.9f))
            });
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        var profile = await db2.People.SingleAsync(p => p.Id == profileId);
        // A voiceprint is optional on a person now, so say out loud that this one has one.
        Assert.NotNull(profile.Embedding);
        Assert.Equal(192, profile.Embedding!.ToArray().Length);
        Assert.Equal(0.5f, profile.Embedding!.ToArray()[5]);
        var speaker = await db2.Speakers.SingleAsync(s => s.Id == speakerId);
        Assert.Equal(0.9f, speaker.Embedding!.ToArray()[0]);
    }

    [Fact]
    public async Task Rank_PutsTheNearestProfileFirst()
    {
        await ClearDirectoryAsync();
        var user = await SeedUser();
        var aliceId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.People.Add(new Person { Id = aliceId, CreatedByUserId = user.Id, RoomId = await RoomOf(db, user.Id), Name = "Alice", Embedding = Vec((0, 1f)) });
            db.People.Add(new Person { Id = Guid.NewGuid(), CreatedByUserId = user.Id, RoomId = await RoomOf(db, user.Id), Name = "Bob", Embedding = Vec((1, 1f)) });
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        // Almost colinear with Alice's vector → tiny cosine distance.
        var ranked = await Identifier(db2).RankAsync(Vec((0, 1f), (1, 0.1f)));

        Assert.Equal(aliceId, ranked[0].PersonId);
        Assert.Equal("Alice", ranked[0].Name);
        Assert.True(ranked[0].Distance < 0.3);
        Assert.True(ranked[1].Distance > ranked[0].Distance, "Bob ranks behind Alice rather than being filtered out");
    }

    /// <summary>A distant voice is not labelled. The rejection now happens in <c>IdentificationRules</c>
    /// rather than inside the identifier, so this asserts it end to end against real pgvector - the ranking
    /// itself deliberately returns the far candidate, and something downstream has to decline it.</summary>
    [Fact]
    public async Task Labelling_LeavesASpeakerAnonymous_WhenTheNearestVoiceIsTooFar()
    {
        await ClearDirectoryAsync();
        var user = await SeedUser();
        await using (var db = fx.CreateDbContext())
        {
            db.People.Add(new Person { Id = Guid.NewGuid(), CreatedByUserId = user.Id, RoomId = await RoomOf(db, user.Id), Name = "Alice", Embedding = Vec((0, 1f)) });
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        // Orthogonal to Alice's vector → cosine distance ≈ 1, far beyond even the confirmation band.
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = Guid.NewGuid(), Label = "SPEAKER_00",
            DisplayName = "SPEAKER_00", Embedding = Vec((1, 1f)),
        };

        await SpeakerLabeling.ApplyAsync(
            [speaker], Identifier(db2), new IdentificationThresholds(0.30, 0.40, 0.05, 3000),
            new Dictionary<string, long> { ["SPEAKER_00"] = 30_000 });

        Assert.Null(speaker.PersonId);
        Assert.Equal("SPEAKER_00", speaker.DisplayName);
        Assert.False(speaker.IdentifiedAuto);
    }

    /// <summary>The directory is platform-wide, so a voiceprint enrolled by one person identifies that human
    /// in everyone's recordings. This is the real pgvector proof of the scope change, and it is the exact
    /// inverse of the assertion it replaced. It is also the privacy consequence of the design: enrolling
    /// someone is a platform-wide act, not a private one.</summary>
    [Fact]
    public async Task Rank_IncludesAVoiceprintEnrolledByAnotherUser()
    {
        await ClearDirectoryAsync();
        var other = await SeedUser();
        var theirsId = Guid.NewGuid();
        await using (var db = fx.CreateDbContext())
        {
            db.People.Add(new Person { Id = theirsId, CreatedByUserId = other.Id, RoomId = await RoomOf(db, other.Id), Name = "Theirs", Embedding = Vec((0, 1f)) });
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        var ranked = await Identifier(db2).RankAsync(Vec((0, 1f)));

        Assert.Equal(theirsId, Assert.Single(ranked).PersonId);
    }

    /// <summary>Someone who asked not to be voice-printed is never a candidate, even while a stale embedding
    /// is still on the row - the filter, not the erase, is what guarantees it.</summary>
    [Fact]
    public async Task Rank_IgnoresAnOptedOutPerson()
    {
        await ClearDirectoryAsync();
        var user = await SeedUser();
        await using (var db = fx.CreateDbContext())
        {
            db.People.Add(new Person
            {
                Id = Guid.NewGuid(), CreatedByUserId = user.Id, RoomId = await RoomOf(db, user.Id),
                Name = "Opted out", Embedding = Vec((0, 1f)), VoiceprintOptOut = true,
            });
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();

        Assert.Empty(await Identifier(db2).RankAsync(Vec((0, 1f))));
    }

    [Fact]
    public async Task DeletingUser_CascadesProfilesAndContributions()
    {
        var user = await SeedUser();
        var profileId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        var speakerId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.People.Add(new Person { Id = profileId, CreatedByUserId = user.Id, RoomId = await RoomOf(db, user.Id), Name = "Alice", Embedding = Vec((0, 1f)) });
            db.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k" });
            db.Speakers.Add(new Speaker { Id = speakerId, RecordingId = recId, Label = "SPEAKER_00", DisplayName = "Alice" });
            db.VoiceSamples.Add(new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = profileId, SpeakerId = speakerId, RecordingId = recId, Embedding = Vec((0, 1f))
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == user.Id));
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        Assert.Empty(await db2.People.Where(p => p.Id == profileId).ToListAsync());
        Assert.Empty(await db2.VoiceSamples.Where(c => c.PersonId == profileId).ToListAsync());
    }

    [Fact]
    public async Task DeletingProfile_NullsSpeakerProfileId_AndCascadesContributions()
    {
        var user = await SeedUser();
        var profileId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        var speakerId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.People.Add(new Person { Id = profileId, CreatedByUserId = user.Id, RoomId = await RoomOf(db, user.Id), Name = "Alice", Embedding = Vec((0, 1f)) });
            db.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k" });
            db.Speakers.Add(new Speaker
            {
                Id = speakerId, RecordingId = recId, Label = "SPEAKER_00", DisplayName = "Alice",
                PersonId = profileId, IdentifiedAuto = true
            });
            db.VoiceSamples.Add(new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = profileId, SpeakerId = speakerId, RecordingId = recId, Embedding = Vec((0, 1f))
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.People.Remove(await db.People.SingleAsync(p => p.Id == profileId));
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        var speaker = await db2.Speakers.SingleAsync(s => s.Id == speakerId);
        Assert.Null(speaker.PersonId); // FK OnDelete SetNull
        Assert.Empty(await db2.VoiceSamples.Where(c => c.PersonId == profileId).ToListAsync());
    }

    private PeopleController ProfilesController(Diariz.Domain.DiarizDbContext db, Guid userId)
    {
        // Destructive writes on the shared directory need ManagePeople now; these tests are about the
        // pgvector behaviour underneath, not the gate (PeopleBiometricGateTests covers that).
        Perms.Grant(db, userId, PlatformPermission.ManagePeople);
        return new(db, new RoomScope(db), new PeopleDirectory(db), new UserPermissions(db), new FakeJobQueue(),
            new FakeAudioClipper(), new FakeAudioStorage(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PeopleController>.Instance, new PlatformSettingsService(db))
        {
            ControllerContext = Http.Context(userId),
        };
    }

    [Fact]
    public async Task RemoveVoiceSample_RecomputesCentroidFromRemaining()
    {
        var user = await SeedUser();
        var profileId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var dropId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            // Centroid starts as the (normalised) mean of e0 and e1. Both speakers are linked to the
            // profile because that is what AssignAsync does before it records a sample - an unlinked
            // speaker no longer counts as training data, so leaving it out made this seed unreachable.
            db.People.Add(new Person { Id = profileId, CreatedByUserId = user.Id, RoomId = await RoomOf(db, user.Id), Name = "Alice", SampleCount = 2, Embedding = Vec((0, 1f)) });
            db.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k" });
            db.Speakers.AddRange(
                new Speaker { Id = s1, RecordingId = recId, Label = "SPEAKER_00", DisplayName = "Alice", PersonId = profileId, Embedding = Vec((0, 1f)) },
                new Speaker { Id = s2, RecordingId = recId, Label = "SPEAKER_01", DisplayName = "Alice", PersonId = profileId, Embedding = Vec((1, 1f)) });
            db.VoiceSamples.AddRange(
                new VoiceSample { Id = Guid.NewGuid(), PersonId = profileId, SpeakerId = s1, RecordingId = recId, Embedding = Vec((0, 1f)) },
                new VoiceSample { Id = dropId, PersonId = profileId, SpeakerId = s2, RecordingId = recId, Embedding = Vec((1, 1f)) });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var result = await ProfilesController(db, user.Id).RemoveVoiceSample(profileId, dropId);
            Assert.IsType<NoContentResult>(result);
        }

        await using var db2 = fx.CreateDbContext();
        var profile = await db2.People.SingleAsync(p => p.Id == profileId);
        Assert.Equal(1, profile.SampleCount);
        // Only the e0 contribution remains → centroid is the unit vector e0.
        Assert.NotNull(profile.Embedding);
        var v = profile.Embedding!.ToArray();
        Assert.Equal(1f, v[0], 3);
        Assert.Equal(0f, v[1], 3);
    }

    [Fact]
    public async Task Merge_CombinesContributions_RecomputesCentroid_AndDeletesSource()
    {
        var user = await SeedUser();
        var targetId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        var targetSpeaker = Guid.NewGuid();
        var sourceSpeaker = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.People.Add(new Person { Id = targetId, CreatedByUserId = user.Id, RoomId = await RoomOf(db, user.Id), Name = "Alice", SampleCount = 1, Embedding = Vec((0, 1f)) });
            db.People.Add(new Person { Id = sourceId, CreatedByUserId = user.Id, RoomId = await RoomOf(db, user.Id), Name = "Allie", SampleCount = 1, Embedding = Vec((1, 1f)) });
            db.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k" });
            db.Speakers.AddRange(
                new Speaker { Id = targetSpeaker, RecordingId = recId, Label = "SPEAKER_00", DisplayName = "Alice", PersonId = targetId, Embedding = Vec((0, 1f)) },
                new Speaker { Id = sourceSpeaker, RecordingId = recId, Label = "SPEAKER_01", DisplayName = "Allie", PersonId = sourceId, IdentifiedAuto = true, Embedding = Vec((1, 1f)) });
            db.VoiceSamples.AddRange(
                new VoiceSample { Id = Guid.NewGuid(), PersonId = targetId, SpeakerId = targetSpeaker, RecordingId = recId, Embedding = Vec((0, 1f)) },
                new VoiceSample { Id = Guid.NewGuid(), PersonId = sourceId, SpeakerId = sourceSpeaker, RecordingId = recId, Embedding = Vec((1, 1f)) });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var result = await ProfilesController(db, user.Id).Merge(targetId, new MergePeopleRequest(sourceId));
            Assert.IsType<NoContentResult>(result);
        }

        await using var db2 = fx.CreateDbContext();
        Assert.Null(await db2.People.FirstOrDefaultAsync(p => p.Id == sourceId));
        var target = await db2.People.SingleAsync(p => p.Id == targetId);
        Assert.Equal(2, target.SampleCount);
        // Both contributions survived the source deletion (reparented, not cascade-deleted).
        Assert.Equal(2, await db2.VoiceSamples.CountAsync(c => c.PersonId == targetId));
        // Centroid is the normalised mean of e0 and e1 → both components ≈ 0.707.
        Assert.NotNull(target.Embedding);
        var v = target.Embedding!.ToArray();
        Assert.Equal(0.7071f, v[0], 3);
        Assert.Equal(0.7071f, v[1], 3);
        // The source's speaker was reassigned to the target.
        var sp = await db2.Speakers.SingleAsync(s => s.Id == sourceSpeaker);
        Assert.Equal(targetId, sp.PersonId);
        Assert.Equal("Alice", sp.DisplayName);
    }

    [Fact]
    public async Task Reidentify_LabelsAnonymousSpeaker_AgainstStoredEmbedding()
    {
        await ClearDirectoryAsync();
        var user = await SeedUser();
        var recId = Guid.NewGuid();
        var speakerId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.People.Add(new Person { Id = Guid.NewGuid(), CreatedByUserId = user.Id, RoomId = await RoomOf(db, user.Id), Name = "Alice", Embedding = Vec((0, 1f)) });
            db.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k" });
            db.Speakers.Add(new Speaker
            {
                Id = speakerId, RecordingId = recId, Label = "SPEAKER_00", DisplayName = "SPEAKER_00",
                Embedding = Vec((0, 1f)) // close to Alice's voiceprint
            });
            await db.SaveChangesAsync();
        }

        // Re-identify uses the speakers' stored embeddings (no re-transcription).
        await using (var db = fx.CreateDbContext())
        {
            var speakers = await db.Speakers.Where(s => s.RecordingId == recId).ToListAsync();
            await SpeakerLabeling.ApplyAsync(
                speakers, Identifier(db), new IdentificationThresholds(0.30, 0.40, 0.05, 3000),
                new Dictionary<string, long> { ["SPEAKER_00"] = 30_000 });
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        var sp = await db2.Speakers.SingleAsync(s => s.Id == speakerId);
        Assert.Equal("Alice", sp.DisplayName);
        Assert.True(sp.IdentifiedAuto);
    }

    [Fact]
    public async Task DeletingRecording_CascadesSpeakers()
    {
        var user = await SeedUser();
        var recId = Guid.NewGuid();
        var speakerId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k" });
            db.Speakers.Add(new Speaker
            {
                Id = speakerId, RecordingId = recId, Label = "SPEAKER_00", DisplayName = "SPEAKER_00",
                Embedding = Vec((0, 1f))
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Recordings.Remove(await db.Recordings.SingleAsync(r => r.Id == recId));
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        Assert.Empty(await db2.Speakers.Where(s => s.Id == speakerId).ToListAsync());
    }

    /// <summary>Ranking is evidence, not a decision. It applies no threshold at all - what to do with a
    /// distance now belongs to <c>IdentificationRules</c>, so the two cannot each hold their own idea of what
    /// "a match" means.</summary>
    [Fact]
    public async Task RankAsync_OrdersPeopleByDistance_AndAppliesNoThreshold()
    {
        await ClearDirectoryAsync();
        var user = await SeedUser();

        await using var db = fx.CreateDbContext();
        var near = new Person { Id = Guid.NewGuid(), Name = "Near", Embedding = Vec((0, 1f)), SampleCount = 1 };
        // Orthogonal: cosine distance 1.0, far beyond any acceptance band. It must still be ranked.
        var far = new Person { Id = Guid.NewGuid(), Name = "Far", Embedding = Vec((1, 1f)), SampleCount = 1 };
        db.People.AddRange(near, far);
        await db.SaveChangesAsync();

        var ranked = await Identifier(db).RankAsync(Vec((0, 1f)), take: 5);

        Assert.Equal(2, ranked.Count);
        Assert.Equal(near.Id, ranked[0].PersonId);
        Assert.Equal(far.Id, ranked[1].PersonId);
        Assert.True(ranked[0].Distance < ranked[1].Distance);
        Assert.True(ranked[1].Distance > 0.9, "an orthogonal voice must still be ranked, not filtered out");
    }

    [Fact]
    public async Task RankAsync_ExcludesOptedOutPeopleAndThoseWithNoVoiceprint()
    {
        // A CosineDistance over a NULL column does nothing useful, and someone who opted out must never be
        // matched at all - that is the whole content of opting out.
        await ClearDirectoryAsync();
        await SeedUser();

        await using var db = fx.CreateDbContext();
        db.People.AddRange(
            new Person { Id = Guid.NewGuid(), Name = "NoPrint", Embedding = null },
            new Person
            {
                Id = Guid.NewGuid(), Name = "OptedOut", Embedding = Vec((0, 1f)),
                SampleCount = 1, VoiceprintOptOut = true,
            });
        await db.SaveChangesAsync();

        Assert.Empty(await Identifier(db).RankAsync(Vec((0, 1f)), take: 5));
    }

    [Fact]
    public async Task RankAsync_TakeLimitsTheResult()
    {
        await ClearDirectoryAsync();
        await SeedUser();

        await using var db = fx.CreateDbContext();
        for (var i = 0; i < 4; i++)
            db.People.Add(new Person
            {
                Id = Guid.NewGuid(), Name = $"P{i}", Embedding = Vec((i, 1f)), SampleCount = 1,
            });
        await db.SaveChangesAsync();

        Assert.Equal(2, (await Identifier(db).RankAsync(Vec((0, 1f)), take: 2)).Count);
    }

    [Fact]
    public async Task RankAsync_ReturnsNothingWhenIdentificationIsDisabled()
    {
        await ClearDirectoryAsync();
        await SeedUser();

        await using var db = fx.CreateDbContext();
        db.People.Add(new Person { Id = Guid.NewGuid(), Name = "Near", Embedding = Vec((0, 1f)), SampleCount = 1 });
        await db.SaveChangesAsync();

        var off = new SpeakerIdentifier(db, Options.Create(new IdentificationOptions { Enabled = false }));

        Assert.Empty(await off.RankAsync(Vec((0, 1f))));
    }
}
