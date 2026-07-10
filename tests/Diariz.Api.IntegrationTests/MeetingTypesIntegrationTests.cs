using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class MeetingTypesIntegrationTests(ContainersFixture fx)
{
    private static MeetingTypesController Build(Diariz.Domain.DiarizDbContext db, Guid userId) =>
        new(db, new UserPermissions(db), new RoomScope(db)) { ControllerContext = Http.Context(userId) };

    [Fact]
    public async Task Seeder_persists_standards_with_jsonb_content_through_real_postgres()
    {
        await using (var db = fx.CreateDbContext())
            await MeetingTypeSeeder.SeedAsync(db);

        await using var verify = fx.CreateDbContext();
        var general = await verify.MeetingTypes.SingleAsync(m => m.Key == MeetingType.GeneralKey);
        var content = MeetingTypeContent.Parse(general.ContentJson);
        Assert.NotEmpty(content.Sections);                 // jsonb round-tripped
        Assert.True(content.Validate().Ok);
    }

    [Fact]
    public async Task Seeder_upgradesALegacyGeneral_ThroughJsonbNormalization()
    {
        // Seed, then strip the Enhanced-notes section back to the pre-notes shape - and let REAL Postgres
        // round-trip the JSON through jsonb, which reformats it (spaces after colons/commas). The upgrade
        // must compare canonically, or it never fires outside the in-memory provider.
        await using (var db = fx.CreateDbContext())
            await MeetingTypeSeeder.SeedAsync(db);

        await using (var db = fx.CreateDbContext())
        {
            var general = await db.MeetingTypes.SingleAsync(m => m.Key == MeetingType.GeneralKey);
            var content = MeetingTypeContent.Parse(general.ContentJson);
            general.ContentJson = (content with
            {
                Sections = content.Sections.Where(s => s.Title != "Enhanced notes").ToList(),
            }).Serialize();
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            // Sanity: jsonb reformatted the stored string (it no longer equals the compact serializer form).
            var stored = await db.MeetingTypes.SingleAsync(m => m.Key == MeetingType.GeneralKey);
            Assert.False(MeetingTypeContent.Parse(stored.ContentJson).HasField("notes"));

            await MeetingTypeSeeder.SeedAsync(db); // next boot upgrades it
        }

        await using var verify = fx.CreateDbContext();
        var upgraded = await verify.MeetingTypes.SingleAsync(m => m.Key == MeetingType.GeneralKey);
        Assert.True(MeetingTypeContent.Parse(upgraded.ContentJson).HasField("notes"));
    }

    [Fact]
    public async Task Deleting_a_meeting_type_nulls_the_recordings_that_used_it()
    {
        Guid userId, typeId, recId;
        await using (var db = fx.CreateDbContext())
        {
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = "u@x.test" };
            var type = new MeetingType
            {
                Id = Guid.NewGuid(), UserId = user.Id, GroupName = "Mine", Title = "Client call",
                ContentJson = new MeetingTypeContent([]).Serialize(),
            };
            var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, BlobKey = "k", MeetingTypeId = type.Id };
            db.AddRange(user, type, rec);
            await db.SaveChangesAsync();
            (userId, typeId, recId) = (user.Id, type.Id, rec.Id);
        }

        await using (var db = fx.CreateDbContext())
        {
            db.MeetingTypes.Remove(await db.MeetingTypes.FindAsync(typeId) ?? throw new InvalidOperationException());
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.Null((await verify.Recordings.FindAsync(recId))!.MeetingTypeId); // SetNull, recording kept
    }
}
