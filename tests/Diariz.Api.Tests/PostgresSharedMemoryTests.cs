using System.Globalization;
using System.Text.RegularExpressions;

namespace Diariz.Api.Tests;

/// <summary>Guards the one relationship between two Postgres settings that is invisible when broken:
/// <c>shm_size</c> must be at least <c>maintenance_work_mem</c>.
///
/// <para>pgvector's <b>parallel</b> HNSW build puts the graph in a dynamic shared memory segment sized from
/// <c>maintenance_work_mem</c>, and in a container that segment lives in <c>/dev/shm</c>, whose size is
/// <c>shm_size</c>. So raising <c>maintenance_work_mem</c> to win the in-memory build path is exactly what
/// makes the build exceed <c>/dev/shm</c> - and the failure is a mid-build
/// <c>could not resize shared memory segment ... No space left on device</c>, nowhere near either setting.</para>
///
/// <para>0.275.1 shipped that inconsistency: <c>shm_size</c> was sized against <c>work_mem</c> (64 MB) and
/// not against <c>maintenance_work_mem</c> (2 GB), so a REINDEX of the chunk embedding index failed on prod
/// (issue #819). Nothing caught it because the two values live in different files and neither looks wrong on
/// its own. This asserts the pair, in every place the repo states one.</para>
///
/// <para>Note what this deliberately is not: a test that the compose file contains particular text. It reads
/// two numbers and compares them, so it fails when the <b>relationship</b> breaks and stays silent when the
/// values merely change - which is the only version of this test worth having.</para></summary>
public class PostgresSharedMemoryTests
{
    private static readonly string DeployDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "deploy");

    /// <summary>A (maintenance_work_mem, shm_size) pair as some file states it, with a label for the
    /// failure message - the whole point is to say WHICH configuration is inconsistent.</summary>
    private sealed record Pairing(string Where, string MaintenanceWorkMem, string ShmSize);

    public static TheoryData<string> ComposeFiles() => new() { "docker-compose.yml", "docker-compose.rocm.yml" };

    [Theory]
    [MemberData(nameof(ComposeFiles))]
    public void ComposeDefaults_GiveDevShmAtLeastMaintenanceWorkMem(string file)
    {
        var text = File.ReadAllText(Path.Combine(DeployDir, file));
        var pairing = new Pairing(
            file,
            DefaultOf(text, "PG_MAINTENANCE_WORK_MEM"),
            ShmSizeOf(text));

        AssertFits(pairing);
    }

    [Fact]
    public void EnvExampleActiveValues_GiveDevShmAtLeastMaintenanceWorkMem()
    {
        var lines = ActiveAssignments(File.ReadAllLines(Path.Combine(DeployDir, ".env.example")));

        AssertFits(new Pairing(
            ".env.example (active values)",
            lines["PG_MAINTENANCE_WORK_MEM"],
            lines["PG_SHM_SIZE"]));
    }

    /// <summary>The worked set for a large host is commented out, so nothing executes it and nothing else
    /// would notice it being wrong - and it is the one an operator copies, which is how #819 reached prod.
    /// It is a configuration whether or not it is switched on.</summary>
    [Fact]
    public void EnvExampleWorkedSet_GivesDevShmAtLeastMaintenanceWorkMem()
    {
        var lines = CommentedAssignments(File.ReadAllLines(Path.Combine(DeployDir, ".env.example")));

        AssertFits(new Pairing(
            ".env.example (the commented worked set for a large host)",
            lines["PG_MAINTENANCE_WORK_MEM"],
            lines["PG_SHM_SIZE"]));
    }

    /// <summary>.env.example repeats the compose defaults so they are visible without reading the compose
    /// file. Two copies of one value agree only by luck, so this pins them together.</summary>
    [Fact]
    public void EnvExampleActiveValues_MatchTheComposeDefaults()
    {
        var compose = File.ReadAllText(Path.Combine(DeployDir, "docker-compose.yml"));
        var active = ActiveAssignments(File.ReadAllLines(Path.Combine(DeployDir, ".env.example")));

        Assert.Equal(DefaultOf(compose, "PG_MAINTENANCE_WORK_MEM"), active["PG_MAINTENANCE_WORK_MEM"]);
        Assert.Equal(ShmSizeOf(compose), active["PG_SHM_SIZE"]);
    }

    private static void AssertFits(Pairing p)
    {
        var mwm = Bytes(p.MaintenanceWorkMem);
        var shm = Bytes(p.ShmSize);

        Assert.True(shm >= mwm,
            $"{p.Where}: shm_size is {p.ShmSize} ({shm:N0} bytes) but maintenance_work_mem is " +
            $"{p.MaintenanceWorkMem} ({mwm:N0} bytes). A parallel HNSW index build allocates a shared " +
            "memory segment sized from maintenance_work_mem, and in a container that segment lives in " +
            "/dev/shm. With shm_size below it, REINDEX on the chunk embedding index dies with \"could not " +
            "resize shared memory segment ... No space left on device\" - see issue #819. Raise shm_size " +
            "to at least maintenance_work_mem.");
    }

    /// <summary>Reads the fallback out of a <c>${VAR:-default}</c> compose substitution.</summary>
    private static string DefaultOf(string composeText, string variable)
    {
        var m = Regex.Match(composeText, Regex.Escape("${" + variable + ":-") + @"([^}]+)\}");
        Assert.True(m.Success, $"No ${{{variable}:-default}} substitution found - has the setting been renamed?");
        return m.Groups[1].Value.Trim();
    }

    private static string ShmSizeOf(string composeText)
    {
        var m = Regex.Match(composeText, @"shm_size:\s*(?:\$\{PG_SHM_SIZE:-)?([^}\s]+)\}?");
        Assert.True(m.Success, "No shm_size found on the postgres service - without it Docker caps /dev/shm at 64 MB.");
        return m.Groups[1].Value.Trim();
    }

    private static Dictionary<string, string> ActiveAssignments(string[] lines) =>
        Assignments(lines.Where(l => !l.TrimStart().StartsWith('#')));

    private static Dictionary<string, string> CommentedAssignments(string[] lines) =>
        Assignments(lines.Where(l => l.TrimStart().StartsWith('#'))
                         .Select(l => l.TrimStart().TrimStart('#')));

    private static Dictionary<string, string> Assignments(IEnumerable<string> lines)
    {
        var found = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            var m = Regex.Match(line.Trim(), @"^(PG_[A-Z_]+)=(\S+)$");
            if (m.Success) found[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return found;
    }

    /// <summary>Parses the size forms Postgres and Docker accept here (<c>1GB</c>, <c>256MB</c>, <c>1gb</c>),
    /// in the binary units both of them mean by them.</summary>
    private static long Bytes(string size)
    {
        var m = Regex.Match(size.Trim(), @"^(\d+)\s*([kmgt]?b?)$", RegexOptions.IgnoreCase);
        Assert.True(m.Success, $"Could not parse '{size}' as a size.");
        var value = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value.ToLowerInvariant() switch
        {
            "k" or "kb" => value * 1024L,
            "m" or "mb" => value * 1024L * 1024L,
            "g" or "gb" => value * 1024L * 1024L * 1024L,
            "t" or "tb" => value * 1024L * 1024L * 1024L * 1024L,
            _ => value,
        };
    }
}
