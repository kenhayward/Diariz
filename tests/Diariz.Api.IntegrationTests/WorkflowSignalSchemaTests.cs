using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class WorkflowSignalSchemaTests(ContainersFixture fx)
{
    [Fact]
    public async Task Signal_links_to_formula_and_deleting_the_signal_removes_the_link_not_the_formula()
    {
        Guid formulaId, signalId;
        await using (var db = fx.CreateDbContext())
        {
            var userId = Guid.NewGuid();
            db.Users.Add(new ApplicationUser { Id = userId, Email = $"{userId:N}@e.com", UserName = $"{userId:N}@e.com" });
            var formula = new Formula { Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId, Name = "F", ContentJson = "{}" };
            var signal = new WorkflowSignal { Id = Guid.NewGuid(), Key = $"k{Guid.NewGuid():N}", Label = "Send to Slack" };
            db.Formulas.Add(formula);
            db.WorkflowSignals.Add(signal);
            db.FormulaWorkflowSignals.Add(new FormulaWorkflowSignal { FormulaId = formula.Id, WorkflowSignalId = signal.Id });
            await db.SaveChangesAsync();

            Assert.True(signal.IsActive); // default
            formulaId = formula.Id;
            signalId = signal.Id;
        }

        // Delete the signal from a FRESH context that never tracked the join row - this proves the join row's
        // removal is the database's ON DELETE CASCADE, not EF's client-side fixup (which would still delete the
        // tracked join row even if the DB-level FK were NoAction).
        await using (var db2 = fx.CreateDbContext())
        {
            var s = await db2.WorkflowSignals.FindAsync(signalId);
            db2.WorkflowSignals.Remove(s!);
            await db2.SaveChangesAsync();
        }

        // Deleting the signal removes the link but leaves the formula - asserted from a third fresh context.
        await using var db3 = fx.CreateDbContext();
        Assert.False(await db3.FormulaWorkflowSignals.AnyAsync(x => x.FormulaId == formulaId));
        Assert.True(await db3.Formulas.AnyAsync(f => f.Id == formulaId));
    }

    [Fact]
    public async Task Subscription_persists_a_signal_filter()
    {
        await using var db = fx.CreateDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = $"{userId:N}@e.com", UserName = $"{userId:N}@e.com" });
        var sub = new WebhookSubscription
        {
            Id = Guid.NewGuid(), OwnerUserId = userId, Scope = WebhookScope.Platform, Name = "P",
            Url = "https://x/y", SecretEncrypted = "c", EventTypes = "formula_result.completed",
            SignalFilter = "post-to-slack",
        };
        db.Webhooks.Add(sub);
        await db.SaveChangesAsync();
        Assert.Equal("post-to-slack", (await db.Webhooks.SingleAsync(s => s.Id == sub.Id)).SignalFilter);
    }
}
