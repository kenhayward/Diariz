using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>The recording an administrator tests AI models against: the stored choice, and the prompt built
/// from that recording.
///
/// <para>These live here rather than in the unit project for two reasons the in-memory provider cannot
/// cover: a nullable column is accepted by that provider whether or not a migration exists, and it ignores
/// ordering inside a filtered query, so a factory that forgot its OrderBy would pass there while handing
/// the model a shuffled meeting.</para></summary>
[Collection(IntegrationCollection.Name)]
public class LlmTestRecordingIntegrationTests(ContainersFixture fx)
{
    private static async Task<Guid> SeedUserAsync(DiarizDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = $"{Guid.NewGuid()}@x.test",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task The_remembered_recording_round_trips_through_real_postgres()
    {
        // A nullable Guid column is trivial in C# and still has to exist in the database: the in-memory
        // provider would accept this property with no migration at all.
        await using var db = fx.CreateDbContext();
        var userId = await SeedUserAsync(db);
        var recordingId = Guid.NewGuid();

        db.UserSettings.Add(new UserSettings { UserId = userId, LlmTestRecordingId = recordingId });
        await db.SaveChangesAsync();

        await using var fresh = fx.CreateDbContext();
        var stored = await fresh.UserSettings.AsNoTracking().FirstAsync(s => s.UserId == userId);
        Assert.Equal(recordingId, stored.LlmTestRecordingId);
    }

    [Fact]
    public async Task Defaults_to_null_for_an_administrator_who_has_never_chosen()
    {
        await using var db = fx.CreateDbContext();
        var userId = await SeedUserAsync(db);

        db.UserSettings.Add(new UserSettings { UserId = userId });
        await db.SaveChangesAsync();

        await using var fresh = fx.CreateDbContext();
        Assert.Null((await fresh.UserSettings.AsNoTracking().FirstAsync(s => s.UserId == userId)).LlmTestRecordingId);
    }
}
