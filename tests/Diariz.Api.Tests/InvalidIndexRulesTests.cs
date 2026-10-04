using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>The decision behind the invalid-index alert (issue #822).
///
/// <para>An index with <c>indisvalid = false</c> is ignored by every query plan while still being
/// maintained on writes, so a failed concurrent build leaves a permanent tax that nothing reports. The
/// trap is that the same state occurs <b>legitimately</b> while a concurrent build is running - and the
/// API applies migrations at startup - so a check that merely looks for invalid indexes cries wolf during
/// deliberate maintenance, which trains the reader to ignore it. Worse than no check.</para>
///
/// <para>Hence two conditions rather than one: an index is reported only if it is invalid on two
/// consecutive observations <b>and</b> Postgres does not currently list a build for it. These tests pin
/// both halves, and the two cases where they interact - a build still running (never reported, however
/// long) and a build that stopped while the index stayed invalid (reported, because that is a failure).</para></summary>
public class InvalidIndexRulesTests
{
    private static IReadOnlySet<string> Nothing => new HashSet<string>();

    private static IndexState Idle(string name) => new(name, BuildInProgress: false);
    private static IndexState Building(string name) => new(name, BuildInProgress: true);

    /// <summary>One sighting is not evidence: a build that began moments before the check looks exactly
    /// like a failed one until it either finishes or is seen again.</summary>
    [Fact]
    public void FirstSighting_IsNotReported()
    {
        var decision = InvalidIndexRules.Decide([Idle("ix_a")], Nothing);

        Assert.Empty(decision.Report);
        Assert.Contains("ix_a", decision.Remember);
    }

    [Fact]
    public void InvalidOnTwoConsecutiveChecks_IsReported()
    {
        var first = InvalidIndexRules.Decide([Idle("ix_a")], Nothing);
        var second = InvalidIndexRules.Decide([Idle("ix_a")], first.Remember);

        Assert.Equal(["ix_a"], second.Report);
    }

    /// <summary>A long `CREATE INDEX CONCURRENTLY` on a large table stays invalid across many checks.
    /// Reporting it would be the false positive that makes the whole alert worthless.</summary>
    [Fact]
    public void BuildStillRunning_IsNeverReported()
    {
        var seen = Nothing;
        for (var check = 0; check < 5; check++)
        {
            var decision = InvalidIndexRules.Decide([Building("ix_a")], seen);
            Assert.Empty(decision.Report);
            seen = decision.Remember;
        }
    }

    /// <summary>The real failure: a build was running, stopped, and left the index invalid behind it.
    /// That is exactly what REINDEX CONCURRENTLY does when it dies, and it must be caught on the first
    /// check after the build disappears rather than waiting for another full interval.</summary>
    [Fact]
    public void BuildThatStoppedLeavingItInvalid_IsReported()
    {
        var during = InvalidIndexRules.Decide([Building("ix_a")], Nothing);
        var after = InvalidIndexRules.Decide([Idle("ix_a")], during.Remember);

        Assert.Equal(["ix_a"], after.Report);
    }

    /// <summary>A build that succeeded, or an index someone dropped: it is simply gone from the query, and
    /// must not be remembered or a later unrelated invalid index with the same name would be reported on
    /// its first sighting.</summary>
    [Fact]
    public void IndexNoLongerInvalid_IsForgotten()
    {
        var first = InvalidIndexRules.Decide([Idle("ix_a")], Nothing);
        var second = InvalidIndexRules.Decide([], first.Remember);

        Assert.Empty(second.Report);
        Assert.Empty(second.Remember);
    }

    /// <summary>Several at once is the normal shape after an interrupted migration, and the order has to be
    /// stable or the same finding reads as a different alert each hour.</summary>
    [Fact]
    public void SeveralReported_AreOrderedByName()
    {
        var seen = new HashSet<string> { "ix_b", "ix_a" };

        var decision = InvalidIndexRules.Decide([Idle("ix_b"), Idle("ix_a")], seen);

        Assert.Equal(["ix_a", "ix_b"], decision.Report);
    }
}
