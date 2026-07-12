using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>Diariz-provided starter formulas are seeded once, create-only, and never overwritten
/// on subsequent boots - mirroring the EnsureGroup pattern in <see cref="Seeder.SeedGroupsAsync"/>.</summary>
public class SeederFormulasTests
{
    [Fact]
    public async Task SeedFormulasAsync_creates_the_four_builtin_formulas()
    {
        using var db = TestDb.Create();

        await Seeder.SeedFormulasAsync(db);

        var formulas = await db.Formulas.ToListAsync();
        Assert.Equal(4, formulas.Count);
        Assert.All(formulas, f =>
        {
            Assert.Equal(FormulaScope.Diariz, f.Scope);
            Assert.True(f.IsBuiltIn);
            Assert.True(f.Enabled);
            Assert.Null(f.OwnerUserId);
        });

        var followUp = formulas.Single(f => f.Name == "Follow-up email");
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary | FormulaContext.Actions, followUp.Context);

        var recap = formulas.Single(f => f.Name == "Meeting recap");
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary, recap.Context);

        var decisions = formulas.Single(f => f.Name == "Decisions & risks");
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Minutes | FormulaContext.Actions, decisions.Context);

        var tone = formulas.Single(f => f.Name == "Tone & sentiment read");
        Assert.Equal(FormulaContext.Transcript, tone.Context);
    }

    [Fact]
    public async Task SeedFormulasAsync_run_twice_does_not_duplicate()
    {
        using var db = TestDb.Create();

        await Seeder.SeedFormulasAsync(db);
        await Seeder.SeedFormulasAsync(db);

        var count = await db.Formulas.CountAsync();
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task SeedFormulasAsync_preserves_an_edited_prompt_on_the_next_run()
    {
        using var db = TestDb.Create();
        await Seeder.SeedFormulasAsync(db);

        var formula = await db.Formulas.SingleAsync(f => f.Name == "Meeting recap");
        formula.Prompt = "Custom admin-edited prompt.";
        await db.SaveChangesAsync();

        await Seeder.SeedFormulasAsync(db);

        var reloaded = await db.Formulas.SingleAsync(f => f.Name == "Meeting recap");
        Assert.Equal("Custom admin-edited prompt.", reloaded.Prompt);
    }
}
