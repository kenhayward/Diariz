using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Real-Postgres checks for the Formula / FormulaResult model: round-trip persistence, cascade
/// from Recording (deleting the recording removes its results), SET NULL from Formula (deleting the
/// formula orphans - but does not delete - its results), Cascade of a Personal formula with its owner,
/// and SET NULL of a result's creator when the author's account is deleted (the document survives).</summary>
[Collection(IntegrationCollection.Name)]
public class FormulasIntegrationTests(ContainersFixture fx)
{
    private async Task<(Guid userId, Guid recId)> SeedUserAndRecording()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = $"{Guid.NewGuid()}@x.test" };
        var rec = new Recording { Id = Guid.NewGuid(), UserId = user.Id, Title = "R", BlobKey = "k" };
        db.Users.Add(user);
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return (user.Id, rec.Id);
    }

    private async Task<Guid> SeedUser()
    {
        await using var db = fx.CreateDbContext();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"{Guid.NewGuid()}@x.test", Email = $"{Guid.NewGuid()}@x.test" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task Formula_And_FormulaResult_RoundTrip()
    {
        var (user, rec) = await SeedUserAndRecording();
        var formulaId = Guid.NewGuid();
        var resultId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.Formulas.Add(new Formula
            {
                Id = formulaId,
                Scope = FormulaScope.Personal,
                OwnerUserId = user,
                Name = "Action Items",
                Description = "Extract action items",
                Prompt = "List all action items from the transcript.",
                Context = FormulaContext.Transcript | FormulaContext.Notes,
                Enabled = true,
                IsBuiltIn = false,
            });
            db.FormulaResults.Add(new FormulaResult
            {
                Id = resultId,
                RecordingId = rec,
                CreatedByUserId = user,
                FormulaId = formulaId,
                Name = "Action Items",
                Text = "- Do the thing",
                Ordinal = 0,
            });
            await db.SaveChangesAsync();
        }

        await using (var verify = fx.CreateDbContext())
        {
            var formula = await verify.Formulas.FindAsync(formulaId);
            Assert.NotNull(formula);
            Assert.Equal(FormulaScope.Personal, formula!.Scope);
            Assert.Equal(FormulaContext.Transcript | FormulaContext.Notes, formula.Context);
            Assert.Equal(user, formula.OwnerUserId);

            var result = await verify.FormulaResults.FindAsync(resultId);
            Assert.NotNull(result);
            Assert.Equal(rec, result!.RecordingId);
            Assert.Equal(formulaId, result.FormulaId);
            Assert.Equal("- Do the thing", result.Text);
        }
    }

    [Fact]
    public async Task DeletingRecording_CascadesItsFormulaResults()
    {
        var (user, rec) = await SeedUserAndRecording();
        var resultId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.FormulaResults.Add(new FormulaResult
            {
                Id = resultId,
                RecordingId = rec,
                CreatedByUserId = user,
                FormulaId = null,
                Name = "Summary",
                Text = "text",
                Ordinal = 0,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Recordings.Remove((await db.Recordings.FindAsync(rec))!);
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.False(await verify.FormulaResults.AnyAsync(r => r.Id == resultId));
    }

    [Fact]
    public async Task DeletingFormula_SetsFormulaIdNull_OnItsResults()
    {
        var (user, rec) = await SeedUserAndRecording();
        var formulaId = Guid.NewGuid();
        var resultId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.Formulas.Add(new Formula
            {
                Id = formulaId,
                Scope = FormulaScope.Platform,
                Name = "Minutes",
                Prompt = "Summarize.",
                Context = FormulaContext.Transcript,
            });
            db.FormulaResults.Add(new FormulaResult
            {
                Id = resultId,
                RecordingId = rec,
                CreatedByUserId = user,
                FormulaId = formulaId,
                Name = "Minutes",
                Text = "text",
                Ordinal = 0,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = fx.CreateDbContext())
        {
            db.Formulas.Remove((await db.Formulas.FindAsync(formulaId))!);
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        var result = await verify.FormulaResults.FindAsync(resultId);
        Assert.NotNull(result);
        Assert.Null(result!.FormulaId);
    }

    [Fact]
    public async Task DeletingUser_CascadesTheirPersonalFormula_ButKeepsResultsOnOthersRecordings()
    {
        // Author A owns a Personal formula and authored a result on user B's recording.
        var (userB, recB) = await SeedUserAndRecording();
        var userA = await SeedUser();
        var personalFormulaId = Guid.NewGuid();
        var resultId = Guid.NewGuid();

        await using (var db = fx.CreateDbContext())
        {
            db.Formulas.Add(new Formula
            {
                Id = personalFormulaId,
                Scope = FormulaScope.Personal,
                OwnerUserId = userA,
                Name = "A's Formula",
                Prompt = "Do a thing.",
                Context = FormulaContext.Transcript,
            });
            db.FormulaResults.Add(new FormulaResult
            {
                Id = resultId,
                RecordingId = recB,
                CreatedByUserId = userA,
                FormulaId = null,
                Name = "A's Formula",
                Text = "A's output on B's recording",
                Ordinal = 0,
            });
            await db.SaveChangesAsync();
        }

        // Deleting A must not violate a FK: their personal formula cascades away, but the document on B's
        // recording survives with its attribution dropped.
        await using (var db = fx.CreateDbContext())
        {
            db.Users.Remove((await db.Users.FindAsync(userA))!);
            await db.SaveChangesAsync();
        }

        await using var verify = fx.CreateDbContext();
        Assert.False(await verify.Formulas.AnyAsync(f => f.Id == personalFormulaId));
        var result = await verify.FormulaResults.FindAsync(resultId);
        Assert.NotNull(result);
        Assert.Null(result!.CreatedByUserId);
        Assert.Equal(recB, result.RecordingId);
        Assert.Equal("A's output on B's recording", result.Text);
    }
}
