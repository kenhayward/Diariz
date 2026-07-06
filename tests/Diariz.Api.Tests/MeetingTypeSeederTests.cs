using Diariz.Api.Services;
using Diariz.Api.Tests.Infrastructure;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Tests;

/// <summary>The app ships a set of standard Platform meeting types (minutes templates), seeded idempotently by
/// their stable <see cref="MeetingType.Key"/> - including the "General Meeting" default that reproduces the
/// original minutes structure and is the fallback for a recording with no explicit type.</summary>
public class MeetingTypeSeederTests
{
    [Fact]
    public async Task SeedAsync_creates_the_standard_platform_types()
    {
        using var db = TestDb.Create();

        await MeetingTypeSeeder.SeedAsync(db);

        var types = await db.MeetingTypes.ToListAsync();
        Assert.Equal(MeetingTypeSeeder.Standards.Count, types.Count);
        Assert.All(types, t => Assert.Null(t.UserId));                      // all Platform (shared)
        Assert.All(types, t => Assert.False(string.IsNullOrWhiteSpace(t.Key)));
        Assert.Contains(types, t => t.Key == MeetingType.GeneralKey);
    }

    [Fact]
    public async Task SeedAsync_general_type_has_valid_non_empty_content()
    {
        using var db = TestDb.Create();
        await MeetingTypeSeeder.SeedAsync(db);

        var general = await db.MeetingTypes.SingleAsync(t => t.Key == MeetingType.GeneralKey);
        var content = MeetingTypeContent.Parse(general.ContentJson);
        Assert.NotEmpty(content.Sections);
        Assert.True(content.Validate().Ok);
        // It renders the canonical actions table (parity with today's minutes).
        Assert.Contains(content.Sections.SelectMany(s => s.Blocks),
            b => b.Kind == TemplateBlock.FieldKind && b.Field == "action_items");
    }

    [Fact]
    public async Task SeedAsync_is_idempotent_and_preserves_admin_edits()
    {
        using var db = TestDb.Create();
        await MeetingTypeSeeder.SeedAsync(db);

        // A platform admin renames a standard type.
        var general = await db.MeetingTypes.SingleAsync(t => t.Key == MeetingType.GeneralKey);
        general.Title = "Renamed by admin";
        await db.SaveChangesAsync();

        await MeetingTypeSeeder.SeedAsync(db);   // second boot

        Assert.Equal(MeetingTypeSeeder.Standards.Count, await db.MeetingTypes.CountAsync()); // no duplicates
        var again = await db.MeetingTypes.SingleAsync(t => t.Key == MeetingType.GeneralKey);
        Assert.Equal("Renamed by admin", again.Title); // insert-if-missing: the edit survives
    }
}
