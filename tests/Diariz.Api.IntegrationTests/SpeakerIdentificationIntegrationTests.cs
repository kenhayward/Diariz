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

    private static SpeakerIdentifier Identifier(Diariz.Domain.DiarizDbContext db, double threshold = 0.4) =>
        new(db, Options.Create(new IdentificationOptions { Enabled = true, Threshold = threshold }));

    [Fact]
    public async Task VectorColumns_RoundTrip_OnProfileAndSpeaker()
    {
        var user = await SeedUser();
        var profileId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        var speakerId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.SpeakerProfiles.Add(new SpeakerProfile
            {
                Id = profileId, UserId = user.Id, Name = "Alice",
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
        var profile = await db2.SpeakerProfiles.SingleAsync(p => p.Id == profileId);
        Assert.Equal(192, profile.Embedding.ToArray().Length);
        Assert.Equal(0.5f, profile.Embedding.ToArray()[5]);
        var speaker = await db2.Speakers.SingleAsync(s => s.Id == speakerId);
        Assert.Equal(0.9f, speaker.Embedding!.ToArray()[0]);
    }

    [Fact]
    public async Task Identify_ReturnsNearestProfile_WithinThreshold()
    {
        var user = await SeedUser();
        var aliceId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.SpeakerProfiles.Add(new SpeakerProfile { Id = aliceId, UserId = user.Id, Name = "Alice", Embedding = Vec((0, 1f)) });
            db.SpeakerProfiles.Add(new SpeakerProfile { Id = Guid.NewGuid(), UserId = user.Id, Name = "Bob", Embedding = Vec((1, 1f)) });
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        // Almost colinear with Alice's vector → tiny cosine distance.
        var match = await Identifier(db2).IdentifyAsync(user.Id, Vec((0, 1f), (1, 0.1f)));

        Assert.NotNull(match);
        Assert.Equal(aliceId, match!.ProfileId);
        Assert.Equal("Alice", match.Name);
        Assert.True(match.Distance < 0.4);
    }

    [Fact]
    public async Task Identify_ReturnsNull_WhenNearestExceedsThreshold()
    {
        var user = await SeedUser();
        await using (var db = fx.CreateDbContext())
        {
            db.SpeakerProfiles.Add(new SpeakerProfile { Id = Guid.NewGuid(), UserId = user.Id, Name = "Alice", Embedding = Vec((0, 1f)) });
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        // Orthogonal to Alice's vector → cosine distance ≈ 1, well above the 0.4 threshold.
        var match = await Identifier(db2).IdentifyAsync(user.Id, Vec((1, 1f)));

        Assert.Null(match);
    }

    [Fact]
    public async Task Identify_IgnoresAnotherUsersProfiles()
    {
        var user = await SeedUser();
        var other = await SeedUser();
        await using (var db = fx.CreateDbContext())
        {
            db.SpeakerProfiles.Add(new SpeakerProfile { Id = Guid.NewGuid(), UserId = other.Id, Name = "Theirs", Embedding = Vec((0, 1f)) });
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        var match = await Identifier(db2).IdentifyAsync(user.Id, Vec((0, 1f)));

        Assert.Null(match); // a perfect match, but it belongs to another user
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
            db.SpeakerProfiles.Add(new SpeakerProfile { Id = profileId, UserId = user.Id, Name = "Alice", Embedding = Vec((0, 1f)) });
            db.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k" });
            db.Speakers.Add(new Speaker { Id = speakerId, RecordingId = recId, Label = "SPEAKER_00", DisplayName = "Alice" });
            db.ProfileContributions.Add(new ProfileContribution
            {
                Id = Guid.NewGuid(), ProfileId = profileId, SpeakerId = speakerId, RecordingId = recId, Embedding = Vec((0, 1f))
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == user.Id));
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        Assert.Empty(await db2.SpeakerProfiles.Where(p => p.Id == profileId).ToListAsync());
        Assert.Empty(await db2.ProfileContributions.Where(c => c.ProfileId == profileId).ToListAsync());
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
            db.SpeakerProfiles.Add(new SpeakerProfile { Id = profileId, UserId = user.Id, Name = "Alice", Embedding = Vec((0, 1f)) });
            db.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k" });
            db.Speakers.Add(new Speaker
            {
                Id = speakerId, RecordingId = recId, Label = "SPEAKER_00", DisplayName = "Alice",
                ProfileId = profileId, IdentifiedAuto = true
            });
            db.ProfileContributions.Add(new ProfileContribution
            {
                Id = Guid.NewGuid(), ProfileId = profileId, SpeakerId = speakerId, RecordingId = recId, Embedding = Vec((0, 1f))
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.SpeakerProfiles.Remove(await db.SpeakerProfiles.SingleAsync(p => p.Id == profileId));
            await db.SaveChangesAsync();
        }

        await using var db2 = fx.CreateDbContext();
        var speaker = await db2.Speakers.SingleAsync(s => s.Id == speakerId);
        Assert.Null(speaker.ProfileId); // FK OnDelete SetNull
        Assert.Empty(await db2.ProfileContributions.Where(c => c.ProfileId == profileId).ToListAsync());
    }

    private SpeakerProfilesController ProfilesController(Diariz.Domain.DiarizDbContext db, Guid userId) =>
        new(db) { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task RemoveContribution_RecomputesCentroidFromRemaining()
    {
        var user = await SeedUser();
        var profileId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var dropId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            // Centroid starts as the (normalised) mean of e0 and e1.
            db.SpeakerProfiles.Add(new SpeakerProfile { Id = profileId, UserId = user.Id, Name = "Alice", SampleCount = 2, Embedding = Vec((0, 1f)) });
            db.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k" });
            db.Speakers.AddRange(
                new Speaker { Id = s1, RecordingId = recId, Label = "SPEAKER_00", DisplayName = "Alice", Embedding = Vec((0, 1f)) },
                new Speaker { Id = s2, RecordingId = recId, Label = "SPEAKER_01", DisplayName = "Alice", Embedding = Vec((1, 1f)) });
            db.ProfileContributions.AddRange(
                new ProfileContribution { Id = Guid.NewGuid(), ProfileId = profileId, SpeakerId = s1, RecordingId = recId, Embedding = Vec((0, 1f)) },
                new ProfileContribution { Id = dropId, ProfileId = profileId, SpeakerId = s2, RecordingId = recId, Embedding = Vec((1, 1f)) });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var result = await ProfilesController(db, user.Id).RemoveContribution(profileId, dropId);
            Assert.IsType<NoContentResult>(result);
        }

        await using var db2 = fx.CreateDbContext();
        var profile = await db2.SpeakerProfiles.SingleAsync(p => p.Id == profileId);
        Assert.Equal(1, profile.SampleCount);
        // Only the e0 contribution remains → centroid is the unit vector e0.
        var v = profile.Embedding.ToArray();
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
            db.SpeakerProfiles.Add(new SpeakerProfile { Id = targetId, UserId = user.Id, Name = "Alice", SampleCount = 1, Embedding = Vec((0, 1f)) });
            db.SpeakerProfiles.Add(new SpeakerProfile { Id = sourceId, UserId = user.Id, Name = "Allie", SampleCount = 1, Embedding = Vec((1, 1f)) });
            db.Recordings.Add(new Recording { Id = recId, UserId = user.Id, BlobKey = "k" });
            db.Speakers.AddRange(
                new Speaker { Id = targetSpeaker, RecordingId = recId, Label = "SPEAKER_00", DisplayName = "Alice", ProfileId = targetId, Embedding = Vec((0, 1f)) },
                new Speaker { Id = sourceSpeaker, RecordingId = recId, Label = "SPEAKER_01", DisplayName = "Allie", ProfileId = sourceId, IdentifiedAuto = true, Embedding = Vec((1, 1f)) });
            db.ProfileContributions.AddRange(
                new ProfileContribution { Id = Guid.NewGuid(), ProfileId = targetId, SpeakerId = targetSpeaker, RecordingId = recId, Embedding = Vec((0, 1f)) },
                new ProfileContribution { Id = Guid.NewGuid(), ProfileId = sourceId, SpeakerId = sourceSpeaker, RecordingId = recId, Embedding = Vec((1, 1f)) });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            var result = await ProfilesController(db, user.Id).Merge(targetId, new MergeSpeakerProfilesRequest(sourceId));
            Assert.IsType<NoContentResult>(result);
        }

        await using var db2 = fx.CreateDbContext();
        Assert.Null(await db2.SpeakerProfiles.FirstOrDefaultAsync(p => p.Id == sourceId));
        var target = await db2.SpeakerProfiles.SingleAsync(p => p.Id == targetId);
        Assert.Equal(2, target.SampleCount);
        // Both contributions survived the source deletion (reparented, not cascade-deleted).
        Assert.Equal(2, await db2.ProfileContributions.CountAsync(c => c.ProfileId == targetId));
        // Centroid is the normalised mean of e0 and e1 → both components ≈ 0.707.
        var v = target.Embedding.ToArray();
        Assert.Equal(0.7071f, v[0], 3);
        Assert.Equal(0.7071f, v[1], 3);
        // The source's speaker was reassigned to the target.
        var sp = await db2.Speakers.SingleAsync(s => s.Id == sourceSpeaker);
        Assert.Equal(targetId, sp.ProfileId);
        Assert.Equal("Alice", sp.DisplayName);
    }

    [Fact]
    public async Task Reidentify_LabelsAnonymousSpeaker_AgainstStoredEmbedding()
    {
        var user = await SeedUser();
        var recId = Guid.NewGuid();
        var speakerId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.SpeakerProfiles.Add(new SpeakerProfile { Id = Guid.NewGuid(), UserId = user.Id, Name = "Alice", Embedding = Vec((0, 1f)) });
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
            await SpeakerLabeling.ApplyAsync(speakers, user.Id, Identifier(db));
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
}
