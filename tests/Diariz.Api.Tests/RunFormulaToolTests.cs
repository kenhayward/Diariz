using System;
using System.Text.Json;
using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Api.Tools;
using Diariz.Domain;
using Diariz.Domain.Entities;

namespace Diariz.Api.Tests;

/// <summary>The run_formula chat tool: resolves a formula by name (scoped to what the user can see) and a
/// recording (via <see cref="RecordingArg"/>), runs it through <see cref="IFormulaRunner"/>, and returns a
/// confirmation + link + the result Markdown. MCP-exposed (accepts recording_id explicitly).</summary>
public class RunFormulaToolTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Guid SeedUser(DiarizDbContext db, string? name = "Alice Smith")
    {
        var id = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = id, Email = $"{id}@example.com", UserName = $"{id}", FullName = name });
        db.SaveChanges();
        return id;
    }

    private static Guid SeedRecording(DiarizDbContext db, Guid userId, string name)
    {
        var id = Guid.NewGuid();
        db.Recordings.Add(new Recording
        {
            Id = id, UserId = userId, Title = name, Name = name, BlobKey = "k", Status = RecordingStatus.Transcribed,
        });
        db.SaveChanges();
        return id;
    }

    private static Formula SeedFormula(
        DiarizDbContext db, string name, FormulaScope scope = FormulaScope.Personal,
        Guid? ownerUserId = null, bool enabled = true)
    {
        var formula = new Formula
        {
            Id = Guid.NewGuid(), Scope = scope, OwnerUserId = ownerUserId, Name = name,
            Prompt = "Summarise.", Context = FormulaContext.Summary, Enabled = enabled,
        };
        db.Formulas.Add(formula);
        db.SaveChanges();
        return formula;
    }

    [Fact]
    public async Task Execute_ResolvesPersonalFormulaByName_AndRecordingById_RunsAndReturnsLinkAndResult()
    {
        using var db = TestDb.Create();
        var me = SeedUser(db);
        var rec = SeedRecording(db, me, "Standup");
        var formula = SeedFormula(db, "Action Items", ownerUserId: me);
        var runner = new FakeFormulaRunner
        {
            Result = new FormulaResult { Id = Guid.NewGuid(), RecordingId = rec, Name = formula.Name, Text = "- do the thing" },
        };

        var result = await new RunFormulaTool(db, runner).ExecuteAsync(
            Args($$"""{"formula":"Action Items","recording_id":"{{rec}}"}"""),
            new ChatToolContext(me, []), default);

        Assert.Equal(1, runner.Calls);
        Assert.Equal((me, rec, formula.Id), runner.LastCall);
        Assert.Contains("Standup", result);
        Assert.Contains("/recordings/", result);           // RecordingLink markdown
        Assert.Contains("do the thing", result);            // the result markdown itself
        Assert.Contains("Action Items", result);
    }

    [Fact]
    public async Task Execute_UnknownFormulaName_ReturnsNoMatchMessage_DoesNotCallRunner()
    {
        using var db = TestDb.Create();
        var me = SeedUser(db);
        var rec = SeedRecording(db, me, "Standup");
        SeedFormula(db, "Action Items", ownerUserId: me);
        var runner = new FakeFormulaRunner();

        var result = await new RunFormulaTool(db, runner).ExecuteAsync(
            Args($$"""{"formula":"Nonexistent","recording_id":"{{rec}}"}"""),
            new ChatToolContext(me, []), default);

        Assert.Equal(0, runner.Calls);
        Assert.Contains("No formula matching", result);
    }

    [Fact]
    public async Task Execute_DisabledPlatformFormula_IsNotVisible_ReturnsNoMatchMessage()
    {
        using var db = TestDb.Create();
        var me = SeedUser(db);
        var rec = SeedRecording(db, me, "Standup");
        SeedFormula(db, "Weekly Digest", scope: FormulaScope.Platform, enabled: false);
        var runner = new FakeFormulaRunner();

        var result = await new RunFormulaTool(db, runner).ExecuteAsync(
            Args($$"""{"formula":"Weekly Digest","recording_id":"{{rec}}"}"""),
            new ChatToolContext(me, []), default);

        Assert.Equal(0, runner.Calls);
        Assert.Contains("No formula matching", result);
    }

    [Fact]
    public async Task Execute_MissingFormulaArg_ReturnsSpecifyFormulaMessage()
    {
        using var db = TestDb.Create();
        var me = SeedUser(db);
        var runner = new FakeFormulaRunner();

        var result = await new RunFormulaTool(db, runner).ExecuteAsync(
            Args("""{}"""), new ChatToolContext(me, []), default);

        Assert.Equal(0, runner.Calls);
        Assert.Contains("Specify a formula", result);
    }

    [Fact]
    public async Task Execute_RunnerThrowsNotConfigured_ReturnsFriendlyNeedsEndpointMessage()
    {
        using var db = TestDb.Create();
        var me = SeedUser(db);
        var rec = SeedRecording(db, me, "Standup");
        var formula = SeedFormula(db, "Action Items", ownerUserId: me);
        var runner = new FakeFormulaRunner { ThrowOnCall = new FormulaNotConfiguredException("no endpoint") };

        var result = await new RunFormulaTool(db, runner).ExecuteAsync(
            Args($$"""{"formula":"Action Items","recording_id":"{{rec}}"}"""),
            new ChatToolContext(me, []), default);

        Assert.Contains("AI endpoint", result);
        Assert.Contains("Settings", result);
    }
}
