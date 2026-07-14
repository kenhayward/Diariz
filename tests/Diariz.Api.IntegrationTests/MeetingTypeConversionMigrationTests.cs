using Diariz.Api.IntegrationTests.Infrastructure;
using Diariz.Api.Services;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Diariz.Api.IntegrationTests;

/// <summary>The destructive one: <c>MeetingTypesPointAtFormulas</c> converts every meeting type's template into a
/// Formula and then <b>drops</b> the column it came from.
///
/// The rest of the integration suite migrates a <i>fresh</i> database, where the conversion SQL runs against zero
/// rows and therefore proves nothing. These tests do the only thing that actually proves it: build a scratch
/// database, migrate it to the migration <b>before</b> this one, insert real old-shape data, and then roll it
/// forward - which is exactly what will happen to a live instance on deploy.</summary>
[Collection(IntegrationCollection.Name)]
public class MeetingTypeConversionMigrationTests(ContainersFixture fx)
{
    /// <summary>The migration immediately before the conversion - the schema a live instance is on today.</summary>
    private const string Before = "20260714163905_AddFormulaContent";
    private const string Conversion = "20260714171555_MeetingTypesPointAtFormulas";

    /// <summary>A scratch database on the shared Postgres container, migrated only as far as <see cref="Before"/>.</summary>
    private async Task<string> ScratchAtOldSchemaAsync()
    {
        var name = $"conv_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(fx.PostgresConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\";", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(fx.PostgresConnectionString) { Database = name }.ToString();
        await using var db = Context(cs);
        await db.Database.GetService<IMigrator>().MigrateAsync(Before);
        return cs;
    }

    private static DiarizDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<DiarizDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .Options);

    private static async Task ExecAsync(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>Insert a meeting type in the OLD shape (carrying its own ContentJson), via raw SQL - the entity no
    /// longer has the column, so it cannot be done through EF.</summary>
    private static async Task SeedOldTypeAsync(
        string cs, string title, string? key, Guid? userId, string contentJson) =>
        await ExecAsync(cs, $"""
            INSERT INTO "MeetingTypes" ("Id", "UserId", "RoomId", "Key", "GroupName", "Title", "Overview",
                                        "Icon", "Color", "ContentJson", "CreatedAt")
            VALUES (gen_random_uuid(),
                    {(userId is { } u ? $"'{u}'" : "NULL")},
                    NULL,
                    {(key is null ? "NULL" : $"'{key}'")},
                    'G', '{title}', 'Some framing.', 'document', '#5C6BC0',
                    '{contentJson.Replace("'", "''")}'::jsonb,
                    now());
            """);

    /// <summary>Insert the owner through EF rather than hand-writing Identity's column list - a Personal meeting
    /// type has an FK to it. Only AspNetUsers is touched, so the not-yet-migrated MeetingTypes shape is irrelevant.</summary>
    private static async Task SeedUserAsync(string cs, Guid id)
    {
        await using var db = Context(cs);
        db.Users.Add(new Domain.Entities.ApplicationUser
        {
            Id = id,
            UserName = $"{id}@x.test",
            NormalizedUserName = $"{id}@X.TEST".ToUpperInvariant(),
            Email = $"{id}@x.test",
            NormalizedEmail = $"{id}@X.TEST".ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString(),
        });
        await db.SaveChangesAsync();
    }

    private const string Template =
        """{"sections":[{"level":1,"title":"Decisions","blocks":[{"kind":"prompt","text":"List the decisions."}]}]}""";

    // The whole point. A live instance's templates must survive the drop - carried into formulas, not discarded.
    [Fact]
    public async Task Converts_each_meeting_types_template_into_a_formula_and_links_it()
    {
        var cs = await ScratchAtOldSchemaAsync();
        await SeedOldTypeAsync(cs, "Client call", key: null, userId: null, Template);

        await using (var db = Context(cs))
            await db.Database.GetService<IMigrator>().MigrateAsync(Conversion);

        await using var after = Context(cs);
        var type = await after.MeetingTypes.Include(m => m.PrimaryFormula).SingleAsync();

        Assert.NotNull(type.PrimaryFormula);
        // The template came across intact - this is the assertion that says "we did not eat your data".
        var content = TemplateContent.Parse(type.PrimaryFormula!.ContentJson);
        Assert.Equal("Decisions", content.Sections[0].Title);
        Assert.Equal("List the decisions.", content.Sections[0].Blocks[0].Text);
    }

    // Scope is preserved, so permissions are unchanged by the conversion. A seeded standard becomes a built-in
    // Diariz formula (undeletable, so it can't be removed out from under the template it drives).
    [Fact]
    public async Task A_seeded_standard_becomes_a_builtin_diariz_formula()
    {
        var cs = await ScratchAtOldSchemaAsync();
        await SeedOldTypeAsync(cs, "General Meeting", key: "general", userId: null, Template);

        await using (var db = Context(cs))
            await db.Database.GetService<IMigrator>().MigrateAsync(Conversion);

        await using var after = Context(cs);
        var f = await after.MeetingTypes.Include(m => m.PrimaryFormula).Select(m => m.PrimaryFormula!).SingleAsync();

        Assert.Equal(Domain.Entities.FormulaScope.Diariz, f.Scope);
        Assert.True(f.IsBuiltIn);
        Assert.Null(f.OwnerUserId);
    }

    [Fact]
    public async Task An_admin_created_platform_type_becomes_a_platform_formula()
    {
        var cs = await ScratchAtOldSchemaAsync();
        await SeedOldTypeAsync(cs, "Board review", key: null, userId: null, Template);

        await using (var db = Context(cs))
            await db.Database.GetService<IMigrator>().MigrateAsync(Conversion);

        await using var after = Context(cs);
        var f = await after.MeetingTypes.Include(m => m.PrimaryFormula).Select(m => m.PrimaryFormula!).SingleAsync();

        Assert.Equal(Domain.Entities.FormulaScope.Platform, f.Scope);
        Assert.False(f.IsBuiltIn);
        Assert.Null(f.OwnerUserId);
    }

    // A user's own template stays theirs: a Personal formula they own. If this regressed to Platform, one user's
    // private template would become visible to the whole instance.
    [Fact]
    public async Task A_users_personal_type_becomes_a_personal_formula_they_own()
    {
        var cs = await ScratchAtOldSchemaAsync();
        var userId = Guid.NewGuid();
        await SeedUserAsync(cs, userId);
        await SeedOldTypeAsync(cs, "My 1:1", key: null, userId: userId, Template);

        await using (var db = Context(cs))
            await db.Database.GetService<IMigrator>().MigrateAsync(Conversion);

        await using var after = Context(cs);
        var f = await after.MeetingTypes.Include(m => m.PrimaryFormula).Select(m => m.PrimaryFormula!).SingleAsync();

        Assert.Equal(Domain.Entities.FormulaScope.Personal, f.Scope);
        Assert.Equal(userId, f.OwnerUserId);
    }

    // A template with quotes/newlines/backslashes must survive: the conversion builds JSON with jsonb_build_*,
    // never string concatenation.
    [Fact]
    public async Task Preserves_a_template_containing_quotes_and_newlines()
    {
        var cs = await ScratchAtOldSchemaAsync();
        var awkward = TemplateContent.FromPrompt("Say \"hello\".\nThen stop.\\done").Serialize();
        await SeedOldTypeAsync(cs, "Awkward", key: null, userId: null, awkward);

        await using (var db = Context(cs))
            await db.Database.GetService<IMigrator>().MigrateAsync(Conversion);

        await using var after = Context(cs);
        var f = await after.MeetingTypes.Include(m => m.PrimaryFormula).Select(m => m.PrimaryFormula!).SingleAsync();

        Assert.Equal("Say \"hello\".\nThen stop.\\done", TemplateContent.Parse(f.ContentJson).BarePrompt());
    }

    [Fact]
    public async Task Drops_the_ContentJson_column_it_converted_from()
    {
        var cs = await ScratchAtOldSchemaAsync();
        await SeedOldTypeAsync(cs, "Client call", key: null, userId: null, Template);

        await using (var db = Context(cs))
            await db.Database.GetService<IMigrator>().MigrateAsync(Conversion);

        var columns = await ScalarAsync<long>(cs, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_name = 'MeetingTypes' AND column_name = 'ContentJson';
            """);
        Assert.Equal(0, columns);
    }
}
