using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>What opting out actually destroys, and what it deliberately leaves alone.
///
/// The rule: the <b>biometric</b> goes, and so does anything derived from it. A name someone typed by hand
/// stays, and keeps pointing at the person - that is their assertion about who was in the room, not something
/// the voiceprint produced, and erasing the voiceprint does not make it untrue.</summary>
public class PeopleOptOutTests
{
    private static async Task<(Person Person, Speaker Auto, Speaker Manual)> SeedAsync(DiarizDbContext db)
    {
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);

        var person = new Person { Id = Guid.NewGuid(), Name = "Alice", SampleCount = 2 };
        db.People.Add(person);

        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Status = RecordingStatus.Transcribed };
        db.Recordings.Add(rec);

        var auto = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Alice", PersonId = person.Id, IdentifiedAuto = true,
        };
        var manual = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01",
            DisplayName = "Alice", PersonId = person.Id, IdentifiedAuto = false,
        };
        db.Speakers.AddRange(auto, manual);
        db.VoiceSamples.Add(new VoiceSample
        {
            Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = auto.Id, RecordingId = rec.Id,
        });
        await db.SaveChangesAsync();
        return (person, auto, manual);
    }

    [Fact]
    public async Task EraseVoiceprint_ClearsTheEmbeddingAndSampleCount()
    {
        using var db = TestDb.Create();
        var (person, _, _) = await SeedAsync(db);

        await new PeopleDirectory(db).EraseVoiceprintAsync(person.Id);

        var reloaded = await db.People.SingleAsync(p => p.Id == person.Id);
        Assert.Null(reloaded.Embedding);
        Assert.Equal(0, reloaded.SampleCount);
    }

    [Fact]
    public async Task EraseVoiceprint_DeletesEveryVoiceSample()
    {
        using var db = TestDb.Create();
        var (person, _, _) = await SeedAsync(db);

        await new PeopleDirectory(db).EraseVoiceprintAsync(person.Id);

        Assert.Equal(0, await db.VoiceSamples.CountAsync(v => v.PersonId == person.Id));
    }

    [Fact]
    public async Task EraseVoiceprint_RevertsAutoAppliedLabels()
    {
        using var db = TestDb.Create();
        var (person, auto, _) = await SeedAsync(db);

        await new PeopleDirectory(db).EraseVoiceprintAsync(person.Id);

        var reloaded = await db.Speakers.SingleAsync(s => s.Id == auto.Id);
        Assert.Equal("SPEAKER_00", reloaded.DisplayName);
        Assert.Null(reloaded.PersonId);
        Assert.False(reloaded.IdentifiedAuto);
    }

    [Fact]
    public async Task EraseVoiceprint_KeepsHandTypedNamesAndTheirLink()
    {
        using var db = TestDb.Create();
        var (person, _, manual) = await SeedAsync(db);

        await new PeopleDirectory(db).EraseVoiceprintAsync(person.Id);

        var reloaded = await db.Speakers.SingleAsync(s => s.Id == manual.Id);
        Assert.Equal("Alice", reloaded.DisplayName);
        Assert.Equal(person.Id, reloaded.PersonId);
    }

    [Fact]
    public async Task EraseVoiceprint_KeepsThePerson()
    {
        using var db = TestDb.Create();
        var (person, _, _) = await SeedAsync(db);

        await new PeopleDirectory(db).EraseVoiceprintAsync(person.Id);

        Assert.NotNull(await db.People.SingleOrDefaultAsync(p => p.Id == person.Id));
    }

    [Fact]
    public async Task EraseVoiceprint_UnknownPerson_DoesNotThrow()
    {
        using var db = TestDb.Create();

        await new PeopleDirectory(db).EraseVoiceprintAsync(Guid.NewGuid());
    }
}
