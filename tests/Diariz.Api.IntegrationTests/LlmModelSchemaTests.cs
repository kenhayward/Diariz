using System.Text.Json.Nodes;
using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>Schema behaviour the in-memory provider does not model: unique indexes, FK delete behaviour,
/// and jsonb round-trips. All of it is why these live here rather than in the unit project.
///
/// <b>Where a model delete is actually refused, and where it is not.</b> Both FKs pointing at LlmModels are
/// DELETE RESTRICT in Postgres, but EF's change tracking gets there first and behaves differently depending
/// on whether the FK is nullable:
///
///   * <c>LlmCallAssignment.LlmModelId</c> is <b>required</b>, so EF refuses as soon as the principal is
///     marked deleted - <c>DbSet.Remove</c> itself throws InvalidOperationException ("the association ...
///     has been severed"), because CascadeDeleteTiming defaults to Immediate. Nothing reaches Postgres, so
///     an assertion has to wrap the Remove call and not just SaveChangesAsync.
///   * <c>PlatformSettings.DefaultLlmModelId</c> is <b>nullable</b>, so when that row is tracked EF quietly
///     issues <c>UPDATE PlatformSettings SET DefaultLlmModelId = NULL</c> ahead of the DELETE. The database
///     constraint never fires and the model IS deleted. The RESTRICT only bites on the untracked path.
///
/// The consequence for callers: <b>the database is a backstop, not the guard</b>. LlmModelsController must
/// check for assignments and for the platform default itself and return 409 - it cannot rely on the delete
/// failing.</summary>
[Collection(IntegrationCollection.Name)]
public class LlmModelSchemaTests(ContainersFixture fx)
{
    private static LlmModel NewModel(string? name = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name ?? $"m-{Guid.NewGuid():N}",
        ApiBase = "http://llm.test/v1",
        ContextLength = 8192,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Round_trips_all_three_parameter_states_through_jsonb()
    {
        // Postgres reformats jsonb, so compare PARSED values - byte-comparing the text never matches.
        await using var db = fx.CreateDbContext();
        var model = NewModel();
        db.LlmModels.Add(model);
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(),
            LlmModelId = model.Id,
            Group = LlmCallGroup.ModelBase,
            ParametersJson = """{"temperature":0.5,"top_k":null}""",
        });
        await db.SaveChangesAsync();

        await using var read = fx.CreateDbContext();
        var row = await read.LlmModelParameters.SingleAsync(p => p.LlmModelId == model.Id);
        var parsed = JsonNode.Parse(row.ParametersJson)!.AsObject();

        Assert.Equal(0.5, parsed["temperature"]!.GetValue<double>());
        // The omit instruction has to survive storage: present as a key...
        Assert.True(parsed.ContainsKey("top_k"));
        // ...and null as its value. If jsonb collapsed these the parameter would silently be inherited.
        Assert.Null(parsed["top_k"]);
    }

    [Fact]
    public async Task Refuses_two_base_rows_for_one_model()
    {
        // The reason Group is non-nullable with ModelBase = 0: Postgres treats NULLs as distinct in a
        // unique index, so a nullable "this is the base" marker would let this through.
        await using var db = fx.CreateDbContext();
        var model = NewModel();
        db.LlmModels.Add(model);
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.ModelBase, ParametersJson = "{}",
        });
        await db.SaveChangesAsync();

        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.ModelBase, ParametersJson = "{}",
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Allows_one_row_per_group_for_the_same_model()
    {
        await using var db = fx.CreateDbContext();
        var model = NewModel();
        db.LlmModels.Add(model);
        foreach (var group in new[] { LlmCallGroup.ModelBase, LlmCallGroup.Tags, LlmCallGroup.Chat })
            db.LlmModelParameters.Add(new LlmModelParameters
            {
                Id = Guid.NewGuid(), LlmModelId = model.Id, Group = group, ParametersJson = "{}",
            });

        await db.SaveChangesAsync();

        await using var read = fx.CreateDbContext();
        Assert.Equal(3, await read.LlmModelParameters.CountAsync(p => p.LlmModelId == model.Id));
    }

    [Fact]
    public async Task Refuses_two_models_with_the_same_name()
    {
        await using var db = fx.CreateDbContext();
        var name = $"m-{Guid.NewGuid():N}";
        db.LlmModels.Add(NewModel(name));
        await db.SaveChangesAsync();

        db.LlmModels.Add(NewModel(name));

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Refuses_to_delete_a_model_an_assignment_points_at()
    {
        // Restrict, not SetNull: a delete that silently re-routed a call group to the default model would
        // change which model serves it with no sign to the administrator.
        //
        // Asserts the GUARANTEE (the model survives), not which layer enforces it - so Remove and
        // SaveChangesAsync are BOTH inside the assertion. That is not tidiness: with the assignment tracked
        // and its FK required, EF refuses at Remove and nothing reaches Postgres, so an assertion wrapped
        // around SaveChangesAsync alone never sees the exception at all.
        await using var db = fx.CreateDbContext();
        var model = NewModel();
        db.LlmModels.Add(model);
        db.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Chat, LlmModelId = model.Id });
        await db.SaveChangesAsync();

        try
        {
            Assert.NotNull(await Record.ExceptionAsync(async () =>
            {
                db.LlmModels.Remove(model);
                await db.SaveChangesAsync();
            }));

            await using var read = fx.CreateDbContext();
            Assert.True(await read.LlmModels.AnyAsync(m => m.Id == model.Id), "the in-use model was deleted");
        }
        finally
        {
            await CleanUp(model.Id);
        }
    }

    [Fact]
    public async Task The_database_itself_refuses_to_delete_an_assigned_model()
    {
        // The untracked path: nothing is loaded, so EF cannot refuse client-side and the statement really
        // does reach Postgres. This is what protects code that deletes a model without loading its
        // assignments - the case the test above cannot reach.
        var id = Guid.NewGuid();
        await using (var seed = fx.CreateDbContext())
        {
            var m = NewModel();
            m.Id = id;
            seed.LlmModels.Add(m);
            seed.LlmCallAssignments.Add(new LlmCallAssignment { Group = LlmCallGroup.Summaries, LlmModelId = id });
            await seed.SaveChangesAsync();
        }

        try
        {
            await using var db = fx.CreateDbContext();
            var stub = new LlmModel { Id = id };
            db.LlmModels.Attach(stub);
            db.LlmModels.Remove(stub);

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            await CleanUp(id);
        }
    }

    [Fact]
    public async Task Nulls_the_platform_default_client_side_rather_than_refusing_the_delete()
    {
        // Documents the gap the API has to close. The FK is nullable, so with the PlatformSettings row
        // tracked EF issues UPDATE ... SET DefaultLlmModelId = NULL before the DELETE, and the RESTRICT it
        // would otherwise hit never fires. If a future EF version or a change to the mapping makes this
        // throw instead, that is an improvement - but this test failing is the signal to revisit the
        // controller guard, not to delete the guard as redundant.
        await using var db = fx.CreateDbContext();
        var model = NewModel();
        db.LlmModels.Add(model);
        await db.SaveChangesAsync();

        (await Settings(db)).DefaultLlmModelId = model.Id;
        await db.SaveChangesAsync();

        try
        {
            db.LlmModels.Remove(model);
            Assert.Null(await Record.ExceptionAsync(() => db.SaveChangesAsync()));

            await using var read = fx.CreateDbContext();
            Assert.False(await read.LlmModels.AnyAsync(m => m.Id == model.Id));
            Assert.Null((await Settings(read)).DefaultLlmModelId);
        }
        finally
        {
            await ClearDefaultAsync();
            await CleanUp(model.Id);
        }
    }

    [Fact]
    public async Task The_database_refuses_to_delete_the_model_the_platform_default_points_at()
    {
        // The untracked path, where the constraint genuinely bites: nothing is loaded, so EF cannot null
        // the FK first and the DELETE really does reach Postgres.
        var id = Guid.NewGuid();
        await using (var seed = fx.CreateDbContext())
        {
            var m = NewModel();
            m.Id = id;
            seed.LlmModels.Add(m);
            await seed.SaveChangesAsync();
            (await Settings(seed)).DefaultLlmModelId = id;
            await seed.SaveChangesAsync();
        }

        try
        {
            await using var db = fx.CreateDbContext();
            var stub = new LlmModel { Id = id };
            db.LlmModels.Attach(stub);
            db.LlmModels.Remove(stub);

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            await ClearDefaultAsync();
            await CleanUp(id);
        }
    }

    [Fact]
    public async Task Deletes_a_models_parameter_rows_with_it()
    {
        await using var db = fx.CreateDbContext();
        var model = NewModel();
        db.LlmModels.Add(model);
        db.LlmModelParameters.Add(new LlmModelParameters
        {
            Id = Guid.NewGuid(), LlmModelId = model.Id, Group = LlmCallGroup.Chat, ParametersJson = "{}",
        });
        await db.SaveChangesAsync();

        db.LlmModels.Remove(model);
        await db.SaveChangesAsync();

        await using var read = fx.CreateDbContext();
        Assert.Empty(await read.LlmModelParameters.Where(p => p.LlmModelId == model.Id).ToListAsync());
    }

    /// <summary>The singleton settings row, created if this database has not seen one yet.</summary>
    private static async Task<PlatformSettings> Settings(Diariz.Domain.DiarizDbContext db)
    {
        var row = await db.PlatformSettings.FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId);
        if (row is null)
        {
            row = new PlatformSettings { Id = PlatformSettings.SingletonId };
            db.PlatformSettings.Add(row);
        }
        return row;
    }

    /// <summary>The picker's per-model description. Round-tripped against real Postgres rather than the
    /// in-memory provider because the length cap and the nullability are schema facts, and the in-memory
    /// provider enforces neither - a unit test would pass with no column at all.</summary>
    [Fact]
    public async Task Description_round_trips_and_is_optional()
    {
        await using var db = fx.CreateDbContext();
        var described = NewModel();
        described.Description = new string('x', 200);
        var undescribed = NewModel();
        db.LlmModels.AddRange(described, undescribed);
        await db.SaveChangesAsync();

        await using var read = fx.CreateDbContext();
        Assert.Equal(new string('x', 200), (await read.LlmModels.FindAsync(described.Id))!.Description);
        Assert.Null((await read.LlmModels.FindAsync(undescribed.Id))!.Description);
    }

    /// <summary>Puts the shared singleton back: every test in this collection sees the same database.</summary>
    private async Task ClearDefaultAsync()
    {
        await using var db = fx.CreateDbContext();
        (await Settings(db)).DefaultLlmModelId = null;
        await db.SaveChangesAsync();
    }

    /// <summary>Removes a model and anything pointing at it. LlmCallAssignment.Group is the primary key, so
    /// a leaked assignment row makes the next test that uses the same group fail on a duplicate key.</summary>
    private async Task CleanUp(Guid modelId)
    {
        await using var db = fx.CreateDbContext();
        db.LlmCallAssignments.RemoveRange(db.LlmCallAssignments.Where(a => a.LlmModelId == modelId));
        await db.SaveChangesAsync();
        db.LlmModels.RemoveRange(db.LlmModels.Where(m => m.Id == modelId));
        await db.SaveChangesAsync();
    }
}
