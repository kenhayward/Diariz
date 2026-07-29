using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>Every user is a person. <see cref="PeopleDirectory"/> owns that invariant: it provisions the
/// linked <see cref="Person"/> on demand and keeps its name and email following the account, so a user is
/// always resolvable as a routable person without a per-site "did you remember" step.
///
/// The in-memory provider <c>Ignore</c>s the pgvector columns, so nothing here asserts on an embedding's
/// contents - only on <see cref="Person.SampleCount"/> and the rows around it. Behaviour that depends on a
/// real NULL embedding lives in the integration project.</summary>
public class PeopleDirectoryTests
{
    private static PeopleDirectory Build(DiarizDbContext db) => new(db);

    // ---- EnsureForUserAsync ----

    [Fact]
    public async Task EnsureForUser_CreatesLinkedPerson_WithNameAndEmail()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId, fullName: "Ada Lovelace");

        var person = await Build(db).EnsureForUserAsync(userId);

        Assert.Equal(userId, person.LinkedUserId);
        Assert.Equal("Ada Lovelace", person.Name);
        Assert.Equal($"{userId:N}@x.test", person.Email);
        Assert.True(person.IsInternal); // an account holder is by definition one of us
        Assert.False(person.VoiceprintOptOut);
        Assert.Equal(0, person.SampleCount);
    }

    [Fact]
    public async Task EnsureForUser_IsIdempotent()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId, fullName: "Ada Lovelace");
        var directory = Build(db);

        var first = await directory.EnsureForUserAsync(userId);
        var second = await directory.EnsureForUserAsync(userId);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.People.CountAsync(p => p.LinkedUserId == userId));
    }

    /// <summary>A user invited but not yet set up has no FullName, and a person with a blank name is
    /// unusable in the assign typeahead. Mirrors <c>RoomScope.Display</c>, which names a personal room the
    /// same way.</summary>
    [Fact]
    public async Task EnsureForUser_FallsBackToEmail_WhenFullNameBlank()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId, fullName: "   ");

        var person = await Build(db).EnsureForUserAsync(userId);

        Assert.Equal($"{userId:N}@x.test", person.Name);
    }

    [Fact]
    public async Task EnsureForUser_UnknownUser_Throws()
    {
        using var db = TestDb.Create();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(db).EnsureForUserAsync(Guid.NewGuid()));
    }

    // ---- SyncFromUserAsync ----

    [Fact]
    public async Task SyncFromUser_UpdatesNameAndEmail()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId, fullName: "Ada Lovelace");
        var directory = Build(db);
        var person = await directory.EnsureForUserAsync(userId);

        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.FullName = "Ada King";
        user.Email = "ada@example.com";
        await db.SaveChangesAsync();

        await directory.SyncFromUserAsync(userId);

        var reloaded = await db.People.SingleAsync(p => p.Id == person.Id);
        Assert.Equal("Ada King", reloaded.Name);
        Assert.Equal("ada@example.com", reloaded.Email);
    }

    /// <summary>Speaker.DisplayName is denormalised - the person's name is copied onto every linked speaker
    /// rather than joined at read time, because segments, exports, email, chat and minutes all read it. A
    /// rename that does not cascade leaves the old name showing on every past transcript.</summary>
    [Fact]
    public async Task SyncFromUser_CascadesNameOntoLinkedSpeakers()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId, fullName: "Ada Lovelace");
        var directory = Build(db);
        var person = await directory.EnsureForUserAsync(userId);

        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Status = RecordingStatus.Transcribed };
        db.Recordings.Add(rec);
        db.Speakers.Add(new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00",
            DisplayName = "Ada Lovelace", PersonId = person.Id, IdentifiedAuto = true,
        });
        var untouched = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_01", DisplayName = "SPEAKER_01",
        };
        db.Speakers.Add(untouched);
        await db.SaveChangesAsync();

        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.FullName = "Ada King";
        await db.SaveChangesAsync();

        await directory.SyncFromUserAsync(userId);

        var linked = await db.Speakers.SingleAsync(s => s.PersonId == person.Id);
        Assert.Equal("Ada King", linked.DisplayName);
        Assert.Equal("SPEAKER_01", (await db.Speakers.SingleAsync(s => s.Id == untouched.Id)).DisplayName);
    }

    [Fact]
    public async Task SyncFromUser_WithNoLinkedPerson_ProvisionsOne()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId, fullName: "Ada Lovelace");

        await Build(db).SyncFromUserAsync(userId);

        Assert.Equal(1, await db.People.CountAsync(p => p.LinkedUserId == userId));
    }

    // ---- RecomputeVoiceprintAsync ----

    /// <summary>The counter must follow the samples. Deleting a recording cascades its speakers and their
    /// voice samples away, and before this existed the person kept a stale SampleCount and a centroid built
    /// from audio that no longer exists.</summary>
    [Fact]
    public async Task RecomputeVoiceprint_WithNoSamples_ClearsSampleCount()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var person = new Person { Id = Guid.NewGuid(), Name = "Ada", SampleCount = 3 };
        db.People.Add(person);
        await db.SaveChangesAsync();

        await Build(db).RecomputeVoiceprintAsync(person.Id);

        Assert.Equal(0, (await db.People.SingleAsync(p => p.Id == person.Id)).SampleCount);
    }

    [Fact]
    public async Task RecomputeVoiceprint_CountsRemainingSamples()
    {
        using var db = TestDb.Create();
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var person = new Person { Id = Guid.NewGuid(), Name = "Ada", SampleCount = 9 };
        db.People.Add(person);
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, Status = RecordingStatus.Transcribed };
        db.Recordings.Add(rec);
        foreach (var label in new[] { "SPEAKER_00", "SPEAKER_01" })
        {
            var speaker = new Speaker
            {
                Id = Guid.NewGuid(), RecordingId = rec.Id, Label = label, DisplayName = "Ada",
                PersonId = person.Id,
            };
            db.Speakers.Add(speaker);
            db.VoiceSamples.Add(new VoiceSample
            {
                Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = speaker.Id, RecordingId = rec.Id,
            });
        }

        await db.SaveChangesAsync();

        await Build(db).RecomputeVoiceprintAsync(person.Id);

        Assert.Equal(2, (await db.People.SingleAsync(p => p.Id == person.Id)).SampleCount);
    }

    [Fact]
    public async Task RecomputeVoiceprint_UnknownPerson_DoesNotThrow()
    {
        using var db = TestDb.Create();

        await Build(db).RecomputeVoiceprintAsync(Guid.NewGuid());
    }
}
