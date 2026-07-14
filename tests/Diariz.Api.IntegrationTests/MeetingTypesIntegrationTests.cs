using Diariz.Api.Controllers;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class MeetingTypesIntegrationTests(ContainersFixture fx)
{
    // The templates are markdown files now, so the seeder needs the catalog installed - as Program.cs does at boot.
    static MeetingTypesIntegrationTests() => Standards.Install();

    private static MeetingTypesController Build(Diariz.Domain.DiarizDbContext db, Guid userId) =>
        new(db, new UserPermissions(db), new RoomScope(db)) { ControllerContext = Http.Context(userId) };

    /// <summary>The formula the seeded General type points at - where its template now lives.</summary>
    private static async Task<Formula> GeneralFormula(DiarizDbContext db)
    {
        var general = await db.MeetingTypes
            .Include(m => m.PrimaryFormula)
            .SingleAsync(m => m.Key == MeetingType.GeneralKey);
        return general.PrimaryFormula!;
    }

    [Fact]
    public async Task Seeder_persists_standards_with_jsonb_content_through_real_postgres()
    {
        await using (var db = fx.CreateDbContext())
            await MeetingTypeSeeder.SeedAsync(db);

        await using var verify = fx.CreateDbContext();
        var general = await verify.MeetingTypes
            .Include(m => m.PrimaryFormula)
            .SingleAsync(m => m.Key == MeetingType.GeneralKey);

        // The template lives on the formula the type points at - and that formula's ContentJson is the jsonb column.
        var content = TemplateContent.Parse(general.PrimaryFormula!.ContentJson);
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
            var formula = await GeneralFormula(db);
            var content = TemplateContent.Parse(formula.ContentJson);
            formula.ContentJson = (content with
            {
                Sections = content.Sections.Where(s => s.Title != "Enhanced notes").ToList(),
            }).Serialize();
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            // Sanity: jsonb reformatted the stored string (it no longer equals the compact serializer form).
            Assert.False(TemplateContent.Parse((await GeneralFormula(db)).ContentJson).HasField("notes"));

            await MeetingTypeSeeder.SeedAsync(db); // next boot upgrades it
        }

        await using var verify = fx.CreateDbContext();
        Assert.True(TemplateContent.Parse((await GeneralFormula(verify)).ContentJson).HasField("notes"));
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
