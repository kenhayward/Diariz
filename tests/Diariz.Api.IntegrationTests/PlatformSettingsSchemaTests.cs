using Diariz.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

[Collection(IntegrationCollection.Name)]
public class PlatformSettingsSchemaTests(ContainersFixture fx)
{
    // The C# property initializers (`= true` / no initializer -> false) only guard EF-issued inserts -
    // they do nothing for the actual Postgres column default. This asserts the migration's
    // `AddColumn(..., defaultValue: ...)` calls are what the database itself carries, straight from
    // the catalog, so a pre-existing/externally-inserted PlatformSettings row (i.e. the deployed
    // singleton) can never silently come back MCP-disabled on deploy - mirrors
    // ApiTokenSchemaTests.Column_default_for_Scope_is_ReadWrite_at_the_SQL_level (Task 1's pattern).
    [Fact]
    public async Task McpAccessEnabled_DefaultsToTrue_AtTheSqlLevel()
    {
        await using var db = fx.CreateDbContext();

        var columnDefault = await db.Database
            .SqlQuery<string>($"""
                SELECT column_default AS "Value" FROM information_schema.columns
                WHERE table_name = 'PlatformSettings' AND column_name = 'McpAccessEnabled'
                """)
            .SingleAsync();

        Assert.Equal("true", columnDefault);
    }

    [Fact]
    public async Task WebhooksEnabled_DefaultsToFalse_AtTheSqlLevel()
    {
        await using var db = fx.CreateDbContext();

        var columnDefault = await db.Database
            .SqlQuery<string>($"""
                SELECT column_default AS "Value" FROM information_schema.columns
                WHERE table_name = 'PlatformSettings' AND column_name = 'WebhooksEnabled'
                """)
            .SingleAsync();

        Assert.Equal("false", columnDefault);
    }
}
