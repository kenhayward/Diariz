namespace Diariz.Api.Services;

/// <summary>One invalid index as the database currently reports it. <paramref name="BuildInProgress"/> is
/// whether Postgres lists a live build for it (<c>pg_stat_progress_create_index</c>), which is the only way
/// to tell a deliberate concurrent build - invalid by design until it finishes - from the wreckage of one
/// that died.</summary>
public sealed record IndexState(string Name, bool BuildInProgress);

/// <summary><paramref name="Report"/> is what to alert on now; <paramref name="Remember"/> is what the next
/// check compares against. Returned together deliberately: the caller deriving "remember" itself would be a
/// second derivation of the same fact, agreeing with this one only by luck.</summary>
public sealed record InvalidIndexDecision(
    IReadOnlyList<string> Report,
    IReadOnlySet<string> Remember);

/// <summary>Decides which invalid indexes are worth an alert. Pure, so the judgement can be tested without
/// a database; the query that feeds it is in <see cref="InvalidIndexMonitor"/> and is Postgres-only.
///
/// <para>Two conditions, because either alone is wrong. <b>Persistence</b> (invalid on two consecutive
/// checks) because a build that started moments ago is indistinguishable from a failed one. <b>No live
/// build</b> because a large `CREATE INDEX CONCURRENTLY` stays invalid across many checks, and reporting it
/// would make the alert cry wolf during exactly the deliberate maintenance it is meant to survive - an alert
/// that fires during normal work trains the reader to ignore it, which is worse than no alert.</para>
///
/// <para>Note the asymmetry: a name seen while building <b>is</b> remembered. So when a build stops and
/// leaves the index invalid - what a failed REINDEX CONCURRENTLY does - it is caught on the very next check
/// rather than after another full interval.</para></summary>
public static class InvalidIndexRules
{
    public static InvalidIndexDecision Decide(
        IReadOnlyCollection<IndexState> invalidNow,
        IReadOnlySet<string> seenLastCheck)
    {
        var report = invalidNow
            .Where(i => !i.BuildInProgress && seenLastCheck.Contains(i.Name))
            .Select(i => i.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Everything still invalid, building or not - see the asymmetry note above. An index that has
        // become valid (or been dropped) simply is not here, so it is forgotten, and a later unrelated
        // index of the same name starts again from one sighting.
        var remember = invalidNow.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);

        return new InvalidIndexDecision(report, remember);
    }
}
