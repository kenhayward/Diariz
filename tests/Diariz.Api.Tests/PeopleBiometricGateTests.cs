using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>The two biometric operations - opting a person out, and erasing their voiceprint - need
/// <see cref="PlatformPermission.ManagePeople"/>, <b>except on the person linked to your own account</b>.
///
/// That exception is the fragile part of the gate: <c>ManagePeople || IsSelf</c> reads like an oversight to
/// anyone tidying permissions later, and it fails silently in the wrong direction - a user quietly loses the
/// ability to withdraw consent for their own biometric, which under GDPR is theirs to withdraw. Hence one
/// test per side of the predicate rather than a single happy-path case.</summary>
public class PeopleBiometricGateTests
{
    private static SpeakerProfilesController Build(DiarizDbContext db, Guid userId)
    {
        Users.Ensure(db, userId);
        return new(db, new Diariz.Api.Services.RoomScope(db), new Diariz.Api.Services.PeopleDirectory(db),
            new Diariz.Api.Services.UserPermissions(db))
        {
            ControllerContext = Http.Context(userId),
        };
    }

    private static async Task<Person> SeedPerson(DiarizDbContext db, Guid? linkedUserId = null)
    {
        var person = new Person
        {
            Id = Guid.NewGuid(), Name = "Alice", SampleCount = 1, LinkedUserId = linkedUserId,
        };
        db.People.Add(person);
        await db.SaveChangesAsync();
        return person;
    }

    // ---- Opting out ----

    [Fact]
    public async Task SetOptOut_OnSomeoneElse_WithoutManagePeople_ReturnsForbid()
    {
        using var db = TestDb.Create();
        var caller = Guid.NewGuid();
        var person = await SeedPerson(db);

        var result = await Build(db, caller).SetVoiceprintOptOut(person.Id, new SetVoiceprintOptOutRequest(true));

        Assert.IsType<ForbidResult>(result);
        Assert.False((await db.People.SingleAsync(p => p.Id == person.Id)).VoiceprintOptOut);
    }

    [Fact]
    public async Task SetOptOut_OnSomeoneElse_WithManagePeople_Succeeds()
    {
        using var db = TestDb.Create();
        var caller = Guid.NewGuid();
        Users.Ensure(db, caller);
        Perms.Grant(db, caller, PlatformPermission.ManagePeople);
        var person = await SeedPerson(db);

        var result = await Build(db, caller).SetVoiceprintOptOut(person.Id, new SetVoiceprintOptOutRequest(true));

        Assert.IsType<NoContentResult>(result);
        Assert.True((await db.People.SingleAsync(p => p.Id == person.Id)).VoiceprintOptOut);
    }

    /// <summary>The GDPR exception: withdrawing consent to process your own biometric data is the data
    /// subject's right, so it must not require an administrator.</summary>
    [Fact]
    public async Task SetOptOut_OnOwnLinkedPerson_WithoutManagePeople_Succeeds()
    {
        using var db = TestDb.Create();
        var caller = Guid.NewGuid();
        Users.Ensure(db, caller);
        var person = await SeedPerson(db, linkedUserId: caller);

        var result = await Build(db, caller).SetVoiceprintOptOut(person.Id, new SetVoiceprintOptOutRequest(true));

        Assert.IsType<NoContentResult>(result);
        Assert.True((await db.People.SingleAsync(p => p.Id == person.Id)).VoiceprintOptOut);
    }

    // ---- Erasing a voiceprint ----

    [Fact]
    public async Task DeleteVoiceprint_OnSomeoneElse_WithoutManagePeople_ReturnsForbid()
    {
        using var db = TestDb.Create();
        var caller = Guid.NewGuid();
        var person = await SeedPerson(db);

        Assert.IsType<ForbidResult>(await Build(db, caller).DeleteVoiceprint(person.Id));
        Assert.Equal(1, (await db.People.SingleAsync(p => p.Id == person.Id)).SampleCount);
    }

    [Fact]
    public async Task DeleteVoiceprint_OnOwnLinkedPerson_WithoutManagePeople_Succeeds()
    {
        using var db = TestDb.Create();
        var caller = Guid.NewGuid();
        Users.Ensure(db, caller);
        var person = await SeedPerson(db, linkedUserId: caller);

        Assert.IsType<NoContentResult>(await Build(db, caller).DeleteVoiceprint(person.Id));
        Assert.Equal(0, (await db.People.SingleAsync(p => p.Id == person.Id)).SampleCount);
    }

    /// <summary>Erasing the biometric is not the same as forgetting who was in the room.</summary>
    [Fact]
    public async Task DeleteVoiceprint_KeepsThePerson()
    {
        using var db = TestDb.Create();
        var caller = Guid.NewGuid();
        Users.Ensure(db, caller);
        var person = await SeedPerson(db, linkedUserId: caller);

        await Build(db, caller).DeleteVoiceprint(person.Id);

        Assert.NotNull(await db.People.SingleOrDefaultAsync(p => p.Id == person.Id));
    }

    // ---- Browsing the directory ----

    /// <summary>The directory is platform-wide, so browsing it exposes every external contact the
    /// organisation has ever recorded. Searching by name to label a speaker stays open to everyone; reading
    /// the list does not.</summary>
    [Fact]
    public async Task List_WithoutManagePeople_ReturnsForbid()
    {
        using var db = TestDb.Create();
        var caller = Guid.NewGuid();
        await SeedPerson(db);

        Assert.IsType<ForbidResult>((await Build(db, caller).List()).Result);
    }

    [Fact]
    public async Task List_WithManagePeople_ReturnsPeopleEnrolledByAnyone()
    {
        using var db = TestDb.Create();
        var caller = Guid.NewGuid();
        Users.Ensure(db, caller);
        Perms.Grant(db, caller, PlatformPermission.ManagePeople);

        var someoneElse = Guid.NewGuid();
        Users.Ensure(db, someoneElse);
        db.People.Add(new Person { Id = Guid.NewGuid(), Name = "Enrolled by someone else", CreatedByUserId = someoneElse });
        await db.SaveChangesAsync();

        var result = await Build(db, caller).List();

        Assert.Contains(Assert.IsAssignableFrom<IReadOnlyList<SpeakerProfileDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value), p => p.Name == "Enrolled by someone else");
    }
}
