using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Diariz.Api.IntegrationTests;

/// <summary>Guards the shape of the people directory against real Postgres - the nullable voiceprint, the
/// one-account-one-person index, the backfill, and above all the table names, which must not follow the CLR
/// rename.</summary>
[Collection(IntegrationCollection.Name)]
public class PeopleSchemaTests(ContainersFixture fx)
{
    /// <summary>The permanent guard against a future contributor "finishing" the rename. Renaming these
    /// tables forces a MaintenanceController.CurrentFormat bump, which hard-rejects every backup archive
    /// taken before that point with no conversion path. If this test fails, that is what is about to
    /// happen - do not update the assertion, revert the rename.</summary>
    [Fact]
    public async Task Tables_KeepTheirOriginalNames_SoOlderBackupsStillRestore()
    {
        var names = await TableNamesAsync();

        Assert.Contains("SpeakerProfiles", names);
        Assert.Contains("ProfileContributions", names);
        Assert.DoesNotContain("People", names);
        Assert.DoesNotContain("VoiceSamples", names);
    }

    [Fact]
    public async Task Embedding_IsNullable_SoAPersonNeedNotHaveAVoiceprint()
    {
        Assert.Equal("YES", await ColumnNullabilityAsync("SpeakerProfiles", "Embedding"));
    }

    [Fact]
    public async Task Person_WithNoVoiceprint_Inserts()
    {
        await using var db = fx.CreateDbContext();
        var person = new Person { Id = Guid.NewGuid(), Name = "No Voiceprint", Email = "nv@x.test" };
        db.People.Add(person);
        await db.SaveChangesAsync();

        var reloaded = await db.People.SingleAsync(p => p.Id == person.Id);
        Assert.Null(reloaded.Embedding);
        Assert.Equal(0, reloaded.SampleCount);
        Assert.False(reloaded.VoiceprintOptOut);
    }

    [Fact]
    public async Task LinkedUserId_IsUnique_SoOneAccountIsOnePerson()
    {
        await using var db = fx.CreateDbContext();
        var user = await SeedUserAsync(db);

        db.People.Add(new Person { Id = Guid.NewGuid(), Name = "First", LinkedUserId = user.Id });
        await db.SaveChangesAsync();

        db.People.Add(new Person { Id = Guid.NewGuid(), Name = "Second", LinkedUserId = user.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>The index is filtered, so the many people with no account (clients, guests) must not collide
    /// with each other on NULL.</summary>
    [Fact]
    public async Task LinkedUserId_AllowsManyUnlinkedPeople()
    {
        await using var db = fx.CreateDbContext();
        db.People.Add(new Person { Id = Guid.NewGuid(), Name = "Guest one" });
        db.People.Add(new Person { Id = Guid.NewGuid(), Name = "Guest two" });

        await db.SaveChangesAsync();
    }

    /// <summary>Provisioning yields exactly one person per account, and calling it twice does not duplicate.
    ///
    /// Deliberately scoped to a user this test created: the integration classes share one database, so
    /// asserting over *every* user would fail on rows other tests seeded directly without provisioning - a
    /// property of the harness, not of the code under test.</summary>
    [Fact]
    public async Task Provisioning_GivesAnActiveUserExactlyOnePerson()
    {
        await using var db = fx.CreateDbContext();
        var user = await SeedUserAsync(db, UserStatus.Active);
        var directory = new PeopleDirectory(db);

        await directory.EnsureForUserAsync(user.Id);
        await directory.EnsureForUserAsync(user.Id);

        Assert.Equal(1, await db.People.CountAsync(p => p.LinkedUserId == user.Id));
    }

    /// <summary>The migration's backfill covers everyone who was Active when it applied, so no pre-existing
    /// active user is left out of the directory. Asserted against the SQL the migration runs rather than the
    /// shared container's accumulated state.</summary>
    [Fact]
    public async Task Backfill_IsIdempotent_AndCoversActiveUsersOnly()
    {
        await using var db = fx.CreateDbContext();
        var active = await SeedUserAsync(db, UserStatus.Active);
        var requested = await SeedUserAsync(db, UserStatus.Requested);

        // Re-running is safe (NOT EXISTS + ON CONFLICT DO NOTHING); run it twice to prove it.
        await db.Database.ExecuteSqlRawAsync(Diariz.Domain.Migrations.PersonForUserBackfill.Sql);
        await db.Database.ExecuteSqlRawAsync(Diariz.Domain.Migrations.PersonForUserBackfill.Sql);

        Assert.Equal(1, await db.People.CountAsync(p => p.LinkedUserId == active.Id));
        // A Requested account may never be granted; provisioning it would fill the directory with people
        // who have never spoken. They get theirs at CompleteSetup, when they become Active.
        Assert.Equal(0, await db.People.CountAsync(p => p.LinkedUserId == requested.Id));
    }

    /// <summary>Deleting an account takes its directory entry with it, which is what deleting a user has
    /// always done to their voiceprints. SetNull would be the alternative, but it would leave an orphan row
    /// still holding the deleted person's name, email and phone - the opposite of erasure.</summary>
    [Fact]
    public async Task DeletingTheLinkedAccount_RemovesThePerson()
    {
        await using var db = fx.CreateDbContext();
        var user = await SeedUserAsync(db);
        var person = new Person { Id = Guid.NewGuid(), Name = "Departing", LinkedUserId = user.Id };
        db.People.Add(person);
        await db.SaveChangesAsync();

        db.Users.Remove(await db.Users.SingleAsync(u => u.Id == user.Id));
        await db.SaveChangesAsync();

        Assert.Null(await db.People.SingleOrDefaultAsync(p => p.Id == person.Id));
    }

    /// <summary>The merge path claims the source's account link for the target. Both rows exist while that
    /// happens, and the filtered unique index permits only one person per account - so the source must
    /// release the link in an earlier round trip than the target claims it. EF Core gives no ordering
    /// guarantee within a single SaveChanges, and the in-memory provider enforces no index at all, so this is
    /// the only layer that can prove the sequence is safe.</summary>
    [Fact]
    public async Task Merge_CarryingTheAccountLink_DoesNotTripTheOneAccountOnePersonIndex()
    {
        await using var db = fx.CreateDbContext();
        var actor = await SeedUserAsync(db);
        var account = await SeedUserAsync(db);
        Perms.Grant(db, actor.Id, PlatformPermission.ManagePeople);

        var target = new Person { Id = Guid.NewGuid(), Name = "Sam" };
        var source = new Person { Id = Guid.NewGuid(), Name = "Samantha", LinkedUserId = account.Id };
        db.People.AddRange(target, source);
        await db.SaveChangesAsync();

        var controller = new Diariz.Api.Controllers.PeopleController(
            db, new RoomScope(db), new PeopleDirectory(db), new UserPermissions(db), new FakeJobQueue(),
            new FakeAudioClipper(), new FakeAudioStorage(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Diariz.Api.Controllers.PeopleController>.Instance, new PlatformSettingsService(db))
        {
            ControllerContext = Http.Context(actor.Id),
        };

        var result = await controller.Merge(target.Id, new Diariz.Api.Contracts.MergePeopleRequest(source.Id));

        Assert.IsType<Microsoft.AspNetCore.Mvc.NoContentResult>(result);
        Assert.Equal(account.Id, (await db.People.SingleAsync(p => p.Id == target.Id)).LinkedUserId);
        Assert.Null(await db.People.SingleOrDefaultAsync(p => p.Id == source.Id));
    }

    private static async Task<ApplicationUser> SeedUserAsync(
        Diariz.Domain.DiarizDbContext db, UserStatus status = UserStatus.Active)
    {
        var email = $"{Guid.NewGuid():N}@x.test";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = email, Email = email, FullName = "Seeded User", Status = status,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private async Task<List<string>> TableNamesAsync()
    {
        await using var conn = new NpgsqlConnection(fx.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public'", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    private async Task<string> ColumnNullabilityAsync(string table, string column)
    {
        await using var conn = new NpgsqlConnection(fx.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT is_nullable FROM information_schema.columns WHERE table_name = @t AND column_name = @c", conn);
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("c", column);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
