using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>In-memory-provider smoke test for the Formula/FormulaResult model: confirms the model builds
/// and basic CRUD round-trips. The in-memory provider does not enforce FK cascade/SetNull behavior - that
/// is covered by <c>FormulasIntegrationTests</c> against real Postgres.</summary>
public class FormulaModelTests
{
    [Fact]
    public async Task Formula_And_FormulaResult_RoundTrip_InMemory()
    {
        await using var db = TestDb.Create();

        var userId = Guid.NewGuid();
        var recId = Guid.NewGuid();
        var formulaId = Guid.NewGuid();
        var resultId = Guid.NewGuid();

        db.Formulas.Add(new Formula
        {
            Id = formulaId,
            Scope = FormulaScope.Diariz,
            Name = "Key Decisions",
            ContentJson = TemplateContent.FromPrompt("Summarize the key decisions made.").Serialize(),
            Context = FormulaContext.Transcript | FormulaContext.Summary,
            Enabled = true,
            IsBuiltIn = true,
        });
        db.FormulaResults.Add(new FormulaResult
        {
            Id = resultId,
            RecordingId = recId,
            CreatedByUserId = userId,
            FormulaId = formulaId,
            Name = "Key Decisions",
            Text = "- Decided X",
            Ordinal = 0,
        });
        await db.SaveChangesAsync();

        var formula = await db.Formulas.FindAsync(formulaId);
        Assert.NotNull(formula);
        Assert.Equal(FormulaScope.Diariz, formula!.Scope);
        Assert.True(formula.IsBuiltIn);
        Assert.Equal(FormulaContext.Transcript | FormulaContext.Summary, formula.Context);

        var result = await db.FormulaResults.FindAsync(resultId);
        Assert.NotNull(result);
        Assert.Equal(formulaId, result!.FormulaId);
        Assert.Equal("- Decided X", result.Text);
    }
}
