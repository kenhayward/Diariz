using Diariz.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.IntegrationTests;

/// <summary>The identification knobs' <b>column</b> defaults, which are not the same thing as their CLR
/// defaults.
///
/// <para>EF scaffolds the CLR zero unless a default is configured, and a threshold of 0 matches nothing at
/// all - identification would be off with no error anywhere. That is not hypothetical: restoring an older
/// backup recreates <c>PlatformSettings</c> from the dump, and the migration then re-adds these columns using
/// their <em>column</em> default. A zero there switches the feature off across a restore.</para></summary>
[Collection(IntegrationCollection.Name)]
public class IdentificationSettingsSchemaTests(ContainersFixture fx)
{
    [Fact]
    public async Task The_columns_carry_working_defaults_not_zero()
    {
        await using var db = fx.CreateDbContext();

        var defaults = new Dictionary<string, string>();
        await using (var cmd = db.Database.GetDbConnection().CreateCommand())
        {
            await db.Database.OpenConnectionAsync();
            cmd.CommandText = """
                SELECT column_name, column_default
                  FROM information_schema.columns
                 WHERE table_name = 'PlatformSettings'
                   AND column_name LIKE 'Identification%'
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                defaults[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }

        Assert.Equal(4, defaults.Count);
        // Read off the real schema the real migration produced, not off a config file - a threshold of 0
        // would be a silently disabled feature, and this is where that would show.
        Assert.StartsWith("0.4", defaults["IdentificationThreshold"]);
        Assert.StartsWith("0.5", defaults["IdentificationConfirmBand"]);
        Assert.StartsWith("0.05", defaults["IdentificationMargin"]);
        Assert.StartsWith("3000", defaults["IdentificationMinSpeechMs"]);
    }
}
