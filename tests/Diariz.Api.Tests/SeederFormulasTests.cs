using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>Diariz-provided starter formulas are seeded once, create-only, and never overwritten on
/// subsequent boots. The specs come from the shipped formulas/*.md (loaded via BuiltInFormulaCatalog).</summary>
public class SeederFormulasTests
{
    private static IReadOnlyList<BuiltInFormulaSpec> Shipped() =>
        BuiltInFormulaCatalog.LoadFrom(Path.Combine(AppContext.BaseDirectory, "formulas"));

    private static readonly IReadOnlyList<BuiltInFormulaSpec> TwoSpecs = new[]
    {
        new BuiltInFormulaSpec("Alpha", "first", "Prompt A", FormulaContext.Transcript),
        new BuiltInFormulaSpec("Beta", null, "Prompt B", FormulaContext.Transcript | FormulaContext.Summary),
    };

    [Fact]
    public async Task SeedFormulasAsync_creates_the_four_builtin_formulas()
    {
        using var db = TestDb.Create();

        await Seeder.SeedFormulasAsync(db, Shipped());

        var formulas = await db.Formulas.ToListAsync();
        Assert.Equal(4, formulas.Count);
        Assert.All(formulas, f =>
        {
            Assert.Equal(FormulaScope.Diariz, f.Scope);
            Assert.True(f.IsBuiltIn);
            Assert.True(f.Enabled);
            Assert.Null(f.OwnerUserId);
        });

        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary | FormulaContext.Actions,
            formulas.Single(f => f.Name == "Follow-up email").Context);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary,
            formulas.Single(f => f.Name == "Meeting recap").Context);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Minutes | FormulaContext.Actions,
            formulas.Single(f => f.Name == "Decisions & risks").Context);
        Assert.Equal(FormulaContext.Transcript,
            formulas.Single(f => f.Name == "Tone & sentiment read").Context);
    }

    [Fact]
    public async Task SeedFormulasAsync_run_twice_does_not_duplicate()
    {
        using var db = TestDb.Create();

        await Seeder.SeedFormulasAsync(db, TwoSpecs);
        await Seeder.SeedFormulasAsync(db, TwoSpecs);

        Assert.Equal(2, await db.Formulas.CountAsync());
    }

    [Fact]
    public async Task SeedFormulasAsync_preserves_an_edited_prompt_on_the_next_run()
    {
        using var db = TestDb.Create();
        await Seeder.SeedFormulasAsync(db, TwoSpecs);

        var formula = await db.Formulas.SingleAsync(f => f.Name == "Beta");
        formula.ContentJson = TemplateContent.FromPrompt("Custom admin-edited prompt.").Serialize();
        await db.SaveChangesAsync();

        await Seeder.SeedFormulasAsync(db, TwoSpecs);

        var reloaded = await db.Formulas.SingleAsync(f => f.Name == "Beta");
        Assert.Equal("Custom admin-edited prompt.", TemplateContent.Parse(reloaded.ContentJson).BarePrompt());
    }
}
