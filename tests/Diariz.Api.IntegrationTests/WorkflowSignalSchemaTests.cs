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
        await using var db = fx.CreateDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = userId, Email = $"{userId:N}@e.com", UserName = $"{userId:N}@e.com" });
        var formula = new Formula { Id = Guid.NewGuid(), Scope = FormulaScope.Personal, OwnerUserId = userId, Name = "F", ContentJson = "{}" };
        var signal = new WorkflowSignal { Id = Guid.NewGuid(), Key = $"k{Guid.NewGuid():N}", Label = "Send to Slack" };
        db.Formulas.Add(formula);
        db.WorkflowSignals.Add(signal);
        db.FormulaWorkflowSignals.Add(new FormulaWorkflowSignal { FormulaId = formula.Id, WorkflowSignalId = signal.Id });
        await db.SaveChangesAsync();

        Assert.True(signal.IsActive); // default

        // Deleting the signal removes the link but leaves the formula.
        db.WorkflowSignals.Remove(signal);
        await db.SaveChangesAsync();
        Assert.False(await db.FormulaWorkflowSignals.AnyAsync(x => x.FormulaId == formula.Id));
        Assert.True(await db.Formulas.AnyAsync(f => f.Id == formula.Id));
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
