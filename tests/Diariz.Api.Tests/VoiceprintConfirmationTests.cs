using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diariz.Api.Tests;

/// <summary>A human vouching for a recording behind a voiceprint: "I have listened, this really is them".
///
/// <para>Deliberately <b>not</b> the same assertion as "trains the voiceprint". Training asks whether audio
/// is good enough to learn from; this asks whether it is the right person. A recording can be genuinely
/// them and still be too noisy to learn from.</para>
///
/// <para>It exists because distance provably cannot tell a second microphone from a second human - which is
/// the whole finding behind the impostor check - so only a person who has listened can settle it. Multi-
/// template voiceprints will gate template-seeding on it; until then it takes a person out of the review
/// queue.</para></summary>
public class VoiceprintConfirmationTests
{
    private static PeopleController Build(DiarizDbContext db, Guid userId) =>
        new(db, new RoomScope(db), new PeopleDirectory(db), new UserPermissions(db), new FakeJobQueue(),
            new FakeAudioClipper(), new FakeAudioStorage(),
            NullLogger<PeopleController>.Instance, new PlatformSettingsService(db))
        {
            ControllerContext = Http.Context(userId),
        };

    private static async Task<(Guid personId, Guid sampleId, Guid userId)> SeedAsync(
        DiarizDbContext db)
    {
        var userId = Guid.NewGuid();
        Users.Ensure(db, userId);
        var person = new Person { Id = Guid.NewGuid(), Name = "Ada", CreatedByUserId = userId };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = userId, BlobKey = "k" };
        var speaker = new Speaker
        {
            Id = Guid.NewGuid(), RecordingId = rec.Id, Label = "SPEAKER_00", DisplayName = "Ada",
            PersonId = person.Id,
        };
        var sample = new VoiceSample
        {
            Id = Guid.NewGuid(), PersonId = person.Id, SpeakerId = speaker.Id, RecordingId = rec.Id,
        };
        db.AddRange(person, rec, speaker, sample);
        // The gate is LinkedUserId == caller, or Manage people. Enrolling someone is not enough on its own.
        Perms.Grant(db, userId, PlatformPermission.ManagePeople);
        await db.SaveChangesAsync();
        return (person.Id, sample.Id, userId);
    }

    [Fact]
    public async Task Confirming_records_who_did_it_and_when()
    {
        // Who matters as much as whether: the point of the gate is that a named human listened, so an
        // anonymous flag would be an assertion nobody is accountable for.
        using var db = TestDb.Create();
        var (personId, sampleId, userId) = await SeedAsync(db);

        var result = await Build(db, userId).SetSampleConfirmed(
            personId, sampleId, new SetSampleConfirmedRequest(true));

        Assert.IsType<NoContentResult>(result);
        var after = await db.VoiceSamples.SingleAsync();
        Assert.NotNull(after.ConfirmedAt);
        Assert.Equal(userId, after.ConfirmedByUserId);
    }

    [Fact]
    public async Task Unconfirming_clears_both_columns()
    {
        // Revocable on purpose. Someone who confirmed in a hurry, or before hearing a later recording that
        // changes their mind, must be able to take it back - and a stale "confirmed by" would be worse than
        // no record at all.
        using var db = TestDb.Create();
        var (personId, sampleId, userId) = await SeedAsync(db);
        await Build(db, userId).SetSampleConfirmed(
            personId, sampleId, new SetSampleConfirmedRequest(true));

        await Build(db, userId).SetSampleConfirmed(
            personId, sampleId, new SetSampleConfirmedRequest(false));

        var after = await db.VoiceSamples.SingleAsync();
        Assert.Null(after.ConfirmedAt);
        Assert.Null(after.ConfirmedByUserId);
    }

    [Fact]
    public async Task Confirming_does_not_change_whether_it_trains()
    {
        // The two assertions are independent, and this is what stops them being quietly merged: confirming
        // that a recording is the right person says nothing about whether its audio is worth learning from.
        using var db = TestDb.Create();
        var (personId, sampleId, userId) = await SeedAsync(db);
        var before = (await db.VoiceSamples.SingleAsync()).ExcludedAt;

        await Build(db, userId).SetSampleConfirmed(
            personId, sampleId, new SetSampleConfirmedRequest(true));

        Assert.Equal(before, (await db.VoiceSamples.SingleAsync()).ExcludedAt);
    }

    [Fact]
    public async Task Someone_without_the_permission_cannot_vouch()
    {
        // Vouching decides what a later release will build templates from, so it is a biometrics-management
        // act, not a reading one. Enrolling the person is not enough on its own.
        using var db = TestDb.Create();
        var (personId, sampleId, _) = await SeedAsync(db);
        var stranger = Guid.NewGuid();
        Users.Ensure(db, stranger);
        await db.SaveChangesAsync();

        var result = await Build(db, stranger).SetSampleConfirmed(
            personId, sampleId, new SetSampleConfirmedRequest(true));

        Assert.IsType<ForbidResult>(result);
        Assert.Null((await db.VoiceSamples.SingleAsync()).ConfirmedAt);
    }

    [Fact]
    public async Task An_unknown_sample_is_not_found()
    {
        using var db = TestDb.Create();
        var (personId, _, userId) = await SeedAsync(db);

        Assert.IsType<NotFoundResult>(await Build(db, userId).SetSampleConfirmed(
            personId, Guid.NewGuid(), new SetSampleConfirmedRequest(true)));
    }

    [Fact]
    public async Task A_sample_belonging_to_a_different_person_is_not_found()
    {
        // The sample id alone must not be enough: a caller who may manage one person's biometrics could
        // otherwise vouch for a recording behind somebody else's voiceprint.
        using var db = TestDb.Create();
        var (_, sampleId, userId) = await SeedAsync(db);
        var other = new Person { Id = Guid.NewGuid(), Name = "Grace", CreatedByUserId = userId };
        db.People.Add(other);
        await db.SaveChangesAsync();

        Assert.IsType<NotFoundResult>(await Build(db, userId).SetSampleConfirmed(
            other.Id, sampleId, new SetSampleConfirmedRequest(true)));
    }
}
