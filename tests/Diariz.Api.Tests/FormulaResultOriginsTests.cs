using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>Origin resolution for a formula result's left-list icon: Diariz/Platform formulas are "official"
/// (no person); Personal formulas attribute to the owner; a deleted formula (FormulaId SET NULL) attributes
/// to the result's creator. The person's display name falls back to email when FullName is null.</summary>
public class FormulaResultOriginsTests
{
    private static async Task<ApplicationUser> AddUser(Diariz.Domain.DiarizDbContext db, string? fullName,
        string email, string? picture)
    {
        var u = new ApplicationUser
        {
            Id = Guid.NewGuid(), UserName = email, Email = email,
            FullName = fullName, PictureUrl = picture,
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    [Fact]
    public async Task Resolves_diariz_platform_personal_and_orphaned_results()
    {
        using var db = TestDb.Create();
        var owner = await AddUser(db, "Ada Lovelace", "ada@x.test", "https://pic/ada.png");
        var noName = await AddUser(db, null, "grace@x.test", null);

        var diariz = new Formula { Id = Guid.NewGuid(), Scope = FormulaScope.Diariz, Name = "Recap", Prompt = "p", IsBuiltIn = true };
        var platform = new Formula { Id = Guid.NewGuid(), Scope = FormulaScope.Platform, Name = "Policy", Prompt = "p" };
        var personal = new Formula { Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = owner.Id, Name = "Mine", Prompt = "p" };
        var personalNoName = new Formula { Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = noName.Id, Name = "Theirs", Prompt = "p" };
        db.Formulas.AddRange(diariz, platform, personal, personalNoName);
        await db.SaveChangesAsync();

        FormulaResult Res(Guid? formulaId, Guid? createdBy) => new()
        {
            Id = Guid.NewGuid(), RecordingId = Guid.NewGuid(), FormulaId = formulaId,
            CreatedByUserId = createdBy, Name = "r", Text = "t",
        };
        var rDiariz = Res(diariz.Id, owner.Id);
        var rPlatform = Res(platform.Id, owner.Id);
        var rPersonal = Res(personal.Id, owner.Id);
        var rNoName = Res(personalNoName.Id, noName.Id);
        var rOrphan = Res(null, owner.Id); // formula deleted -> attribute to creator

        var origins = await FormulaResultOrigins.ResolveAsync(
            db, new[] { rDiariz, rPlatform, rPersonal, rNoName, rOrphan });

        Assert.Equal("diariz", origins[rDiariz.Id].Kind);
        Assert.Null(origins[rDiariz.Id].PersonName);

        Assert.Equal("platform", origins[rPlatform.Id].Kind);
        Assert.Null(origins[rPlatform.Id].PersonName);

        Assert.Equal("personal", origins[rPersonal.Id].Kind);
        Assert.Equal("Ada Lovelace", origins[rPersonal.Id].PersonName);
        Assert.Equal("https://pic/ada.png", origins[rPersonal.Id].PersonPictureUrl);

        Assert.Equal("personal", origins[rNoName.Id].Kind);
        Assert.Equal("grace@x.test", origins[rNoName.Id].PersonName); // FullName null -> email
        Assert.Null(origins[rNoName.Id].PersonPictureUrl);

        Assert.Equal("personal", origins[rOrphan.Id].Kind);
        Assert.Equal("Ada Lovelace", origins[rOrphan.Id].PersonName); // creator
    }
}
