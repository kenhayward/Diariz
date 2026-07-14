using Diariz.Api.Services;

namespace Diariz.Api.Tests.Infrastructure;

/// <summary>Installs the <b>real shipped</b> standard meeting types into <see cref="MeetingTypeSeeder"/>.
///
/// <para>The templates are markdown files now, and the API project copies <c>meeting-types/*.md</c> into the test
/// output alongside its own. So a test that seeds runs against the content that actually ships - if a file is
/// malformed, or a prompt is mangled, the seeder tests fail rather than passing against a fixture that only
/// resembles it.</para></summary>
public static class Standards
{
    private static readonly Lazy<IReadOnlyList<StandardMeetingType>> Loaded = new(() =>
        MeetingTypeCatalog.LoadFrom(Path.Combine(AppContext.BaseDirectory, "meeting-types")));

    /// <summary>The shipped standards, parsed from the markdown files.</summary>
    public static IReadOnlyList<StandardMeetingType> All => Loaded.Value;

    /// <summary>Install them, as <c>Program.cs</c> does at boot. Call before <c>MeetingTypeSeeder.SeedAsync</c>.</summary>
    public static void Install() => MeetingTypeSeeder.UseStandards(Loaded.Value);
}
