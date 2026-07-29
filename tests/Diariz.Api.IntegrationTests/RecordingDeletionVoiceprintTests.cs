using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Diariz.Api.IntegrationTests;

/// <summary>Deleting a recording cascades Recording -> Speakers -> VoiceSamples, which silently removes the
/// training behind someone's voiceprint. Before the directory work, nothing recomputed afterwards, so a
/// person could report SampleCount = 3 with zero samples and keep matching against a centroid built from
/// audio that no longer existed.
///
/// This cannot be a unit test: the in-memory provider does not enforce the cascade, so the samples would
/// still be there and the bug would not reproduce.</summary>
[Collection(IntegrationCollection.Name)]
public class RecordingDeletionVoiceprintTests(ContainersFixture fx)
{
    private static Vector Vec(float first)
    {
        var a = new float[192];
        a[0] = first;
        return new Vector(a);
    }

    [Fact]
    public async Task DeletingTheOnlyContributingRecording_ClearsTheVoiceprint()
    {
        await using var db = fx.CreateDbContext();
        var (person, recordingId) = await SeedEnrolledPersonAsync(db);

        // Sanity: the fixture is what the bug needs - one person, one sample, counted.
        Assert.Equal(1, (await db.People.SingleAsync(p => p.Id == person)).SampleCount);

        await DeleteRecordingAndRecomputeAsync(db, recordingId, person);

        var reloaded = await db.People.SingleAsync(p => p.Id == person);
        Assert.Equal(0, reloaded.SampleCount);
        Assert.Null(reloaded.Embedding);
    }

    [Fact]
    public async Task DeletingOneOfTwoContributingRecordings_LeavesTheVoiceprintWithTheRemainingSample()
    {
        await using var db = fx.CreateDbContext();
        var (person, firstRecording) = await SeedEnrolledPersonAsync(db);
        await AddSampleFromNewRecordingAsync(db, person);

        var before = await db.People.SingleAsync(p => p.Id == person);
        Assert.Equal(2, before.SampleCount);

        await DeleteRecordingAndRecomputeAsync(db, firstRecording, person);

        var reloaded = await db.People.SingleAsync(p => p.Id == person);
        Assert.Equal(1, reloaded.SampleCount);
        Assert.NotNull(reloaded.Embedding);
    }

    /// <summary>The person survives regardless - they were in a meeting, and deleting the recording does not
    /// change that. Only the biometric derived from it goes.</summary>
    [Fact]
    public async Task DeletingTheRecording_KeepsThePersonRow()
    {
        await using var db = fx.CreateDbContext();
        var (person, recordingId) = await SeedEnrolledPersonAsync(db);

        await DeleteRecordingAndRecomputeAsync(db, recordingId, person);

        Assert.NotNull(await db.People.SingleOrDefaultAsync(p => p.Id == person));
    }

    /// <summary>Mirrors what RecordingsController.Delete does: note the affected people before the cascade
    /// takes the rows away, then recompute after.</summary>
    private static async Task DeleteRecordingAndRecomputeAsync(
        Diariz.Domain.DiarizDbContext db, Guid recordingId, Guid personId)
    {
        var affected = await db.Speakers
            .Where(s => s.RecordingId == recordingId && s.PersonId != null)
            .Select(s => s.PersonId!.Value)
            .Distinct()
            .ToListAsync();
        Assert.Contains(personId, affected);

        db.Recordings.Remove(await db.Recordings.SingleAsync(r => r.Id == recordingId));
        await db.SaveChangesAsync();

        foreach (var id in affected)
            await new PeopleDirectory(db).RecomputeVoiceprintAsync(id);
    }

    private static async Task<(Guid PersonId, Guid RecordingId)> SeedEnrolledPersonAsync(
        Diariz.Domain.DiarizDbContext db)
    {
        var email = $"{Guid.NewGuid():N}@x.test";
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email };
        db.Users.Add(user);

        var person = new Person
        {
            Id = Guid.NewGuid(), Name = "Enrolled", CreatedByUserId = user.Id,
            Embedding = Vec(1f), SampleCount = 1,
        };
        db.People.Add(person);
        await db.SaveChangesAsync();

        var recordingId = await AddSampleFromNewRecordingAsync(db, person.Id, user.Id, bumpCount: false);
        return (person.Id, recordingId);
    }

    private static async Task<Guid> AddSampleFromNewRecordingAsync(
        Diariz.Domain.DiarizDbContext db, Guid personId, Guid? userId = null, bool bumpCount = true)
    {
        userId ??= (await db.People.SingleAsync(p => p.Id == personId)).CreatedByUserId!.Value;

        var rec = new Recording
        {
            Id = Guid.NewGuid(), UserId = userId.Value, Status = RecordingStatus.Transcribed,
            Title = "Contributing recording", BlobKey = $"k/{Guid.NewGuid():N}",
        };
        db.Recordings.Add(rec);

        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Enrolled",
            PersonId = personId, Embedding = Vec(1f),
        };
        db.Speakers.Add(speaker);
        db.VoiceSamples.Add(new VoiceSample
        {
            Id = Guid.NewGuid(), PersonId = personId, SpeakerId = speaker.Id, RecordingId = rec.Id,
            Embedding = Vec(1f),
        });

        if (bumpCount)
        {
            var person = await db.People.SingleAsync(p => p.Id == personId);
            person.SampleCount += 1;
        }

        await db.SaveChangesAsync();
        return rec.Id;
    }
}
