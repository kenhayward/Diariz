using Diariz.Api.Contracts;
using Diariz.Api.Controllers;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Diariz.Api.Tests;

public class WorkflowSignalsControllerTests
{
    [Fact]
    public async Task Create_persists_and_list_active_excludes_inactive()
    {
        var db = TestDb.Create();
        var c = new WorkflowSignalsController(db) { ControllerContext = Http.Context(Guid.NewGuid()) };
        await c.Create(new CreateWorkflowSignalRequest("post-to-slack", "Send to Slack", "desc"));
        var inactive = new WorkflowSignal { Id = Guid.NewGuid(), Key = "off", Label = "Off", IsActive = false };
        db.WorkflowSignals.Add(inactive); await db.SaveChangesAsync();

        var active = (await c.ListActive()).Value!;
        Assert.Single(active);
        Assert.Equal("post-to-slack", active[0].Key);
    }

    [Fact]
    public async Task Create_rejects_duplicate_key()
    {
        var db = TestDb.Create();
        var c = new WorkflowSignalsController(db) { ControllerContext = Http.Context(Guid.NewGuid()) };
        await c.Create(new CreateWorkflowSignalRequest("dup", "A", null));
        var res = await c.Create(new CreateWorkflowSignalRequest("dup", "B", null));
        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task ListAll_includes_inactive_signals()
    {
        var db = TestDb.Create();
        var c = new WorkflowSignalsController(db) { ControllerContext = Http.Context(Guid.NewGuid()) };
        await c.Create(new CreateWorkflowSignalRequest("active-one", "Active One", null));
        db.WorkflowSignals.Add(new WorkflowSignal { Id = Guid.NewGuid(), Key = "off", Label = "Off", IsActive = false });
        await db.SaveChangesAsync();

        var all = (await c.ListAll()).Value!;
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Update_toggles_IsActive_and_leaves_Key_unchanged()
    {
        var db = TestDb.Create();
        var c = new WorkflowSignalsController(db) { ControllerContext = Http.Context(Guid.NewGuid()) };
        var created = (await c.Create(new CreateWorkflowSignalRequest("post-to-slack", "Send to Slack", "desc"))).Value!;

        var updated = (await c.Update(created.Id, new UpdateWorkflowSignalRequest("New Label", "new desc", false))).Value!;

        Assert.Equal("post-to-slack", updated.Key);
        Assert.Equal("New Label", updated.Label);
        Assert.Equal("new desc", updated.Description);
        Assert.False(updated.IsActive);
    }

    [Fact]
    public async Task Delete_removes_the_signal()
    {
        var db = TestDb.Create();
        var c = new WorkflowSignalsController(db) { ControllerContext = Http.Context(Guid.NewGuid()) };
        var created = (await c.Create(new CreateWorkflowSignalRequest("post-to-slack", "Send to Slack", null))).Value!;

        var result = await c.Delete(created.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(db.WorkflowSignals);
    }

    [Fact]
    public async Task Create_rejects_key_with_invalid_characters()
    {
        var db = TestDb.Create();
        var c = new WorkflowSignalsController(db) { ControllerContext = Http.Context(Guid.NewGuid()) };
        var res = await c.Create(new CreateWorkflowSignalRequest("Not Valid!", "Label", null));
        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task Create_rejects_missing_label()
    {
        var db = TestDb.Create();
        var c = new WorkflowSignalsController(db) { ControllerContext = Http.Context(Guid.NewGuid()) };
        var res = await c.Create(new CreateWorkflowSignalRequest("some-key", " ", null));
        Assert.IsType<BadRequestObjectResult>(res.Result);
    }
}
