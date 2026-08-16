using Diariz.Api.Contracts;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>A dimension the roll-up summary (<c>LlmUsageController.Summary</c>) can group by. Deliberately
/// a closed enum, not a free-form string carried through to the query - <see cref="LlmUsageQuery.TryResolveGroupBy"/>
/// is the only place a query-string token becomes one of these.</summary>
public enum LlmUsageGroupDimension
{
    User,
    Model,
    Kind,
}

/// <summary>Shared filter/sort primitives for the LLM usage viewer. Every endpoint over
/// <c>LlmCalls</c> composes its query through <see cref="Apply"/> and resolves its sort key through
/// <see cref="TryResolveSort"/>, so there is exactly one place that decides what is filterable and
/// exactly one whitelist of what is sortable.</summary>
public static class LlmUsageQuery
{
    /// <summary>Column names a caller may sort by, keyed by the camelCase name a query string sends.
    /// Resolved through this lookup and never interpolated into SQL - a query string is untrusted
    /// input, and this is the only defence that matters.
    ///
    /// <c>tokensPerSecond</c> is deliberately absent: it is derived (SUM(completion tokens) /
    /// SUM(duration) across the aggregated rows), not a column on <see cref="LlmCall"/>, so there is
    /// no single row value to sort by.</summary>
    private static readonly Dictionary<string, string> SortWhitelist = new()
    {
        ["startedAt"] = nameof(LlmCall.StartedAt),
        ["durationMs"] = nameof(LlmCall.DurationMs),
        ["promptTokens"] = nameof(LlmCall.PromptTokens),
        ["completionTokens"] = nameof(LlmCall.CompletionTokens),
        ["totalTokens"] = nameof(LlmCall.TotalTokens),
        ["kind"] = nameof(LlmCall.Kind),
        ["model"] = nameof(LlmCall.Model),
        ["userEmail"] = nameof(LlmCall.UserEmail),
    };

    /// <summary>Dimension names a caller may group the roll-up summary by, keyed by the token a query
    /// string sends (a comma-separated list, e.g. <c>groupBy=user,model</c>). Same discipline as
    /// <see cref="SortWhitelist"/>: a lookup, never string interpolation, and an unrecognised token is
    /// rejected rather than silently dropped - silently ignoring it would show the administrator a
    /// different report from the one they asked for.</summary>
    private static readonly Dictionary<string, LlmUsageGroupDimension> GroupByWhitelist = new()
    {
        ["user"] = LlmUsageGroupDimension.User,
        ["model"] = LlmUsageGroupDimension.Model,
        ["kind"] = LlmUsageGroupDimension.Kind,
    };

    /// <summary>Composes the shared filter predicate. <paramref name="filter"/>.From defaults to 30
    /// days before <paramref name="nowUtc"/> when null - see the "why" on <see cref="LlmUsageFilter"/>.
    /// Null and empty arrays both mean "no filter on this dimension".</summary>
    public static IQueryable<LlmCall> Apply(IQueryable<LlmCall> source, LlmUsageFilter filter, DateTimeOffset nowUtc)
    {
        var from = filter.From ?? nowUtc.AddDays(-30);
        source = source.Where(c => c.StartedAt >= from);

        if (filter.To is { } to)
            source = source.Where(c => c.StartedAt <= to);

        if (filter.UserIds is { Length: > 0 } userIds)
            source = source.Where(c => c.UserId != null && userIds.Contains(c.UserId.Value));

        if (filter.Kinds is { Length: > 0 } kinds)
            source = source.Where(c => kinds.Contains(c.Kind));

        if (filter.Models is { Length: > 0 } models)
            source = source.Where(c => models.Contains(c.Model));

        source = filter.Outcome switch
        {
            "ok" => source.Where(c => c.Success),
            "failed" => source.Where(c => !c.Success),
            _ => source, // null, "all", or anything else: no filter
        };

        if (filter.RecordingId is { } recordingId)
            source = source.Where(c => c.RecordingId == recordingId);

        if (filter.SectionId is { } sectionId)
            source = source.Where(c => c.SectionId == sectionId);

        return source;
    }

    /// <summary>Resolves a query-string sort key to its canonical column name via whitelist lookup.
    /// False for anything not in <see cref="SortWhitelist"/>, including null and empty.</summary>
    public static bool TryResolveSort(string? sort, out string column)
    {
        if (sort is null)
        {
            column = string.Empty;
            return false;
        }

        return SortWhitelist.TryGetValue(sort, out column!);
    }

    /// <summary>Resolves a comma-separated <c>groupBy</c> query-string value (e.g. <c>"user,model"</c>)
    /// into the distinct set of dimensions it names, via whitelist lookup on each token. False - not a
    /// best-effort partial parse - for null/empty/whitespace-only input, or if ANY token fails to
    /// resolve, so one bad token in an otherwise-valid list rejects the whole request rather than
    /// silently grouping by less than what was asked for. Whitespace around each token is trimmed;
    /// duplicate tokens collapse rather than error.</summary>
    public static bool TryResolveGroupBy(string? groupBy, out LlmUsageGroupDimension[] dimensions)
    {
        dimensions = [];
        if (string.IsNullOrWhiteSpace(groupBy))
            return false;

        var tokens = groupBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        var resolved = new List<LlmUsageGroupDimension>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!GroupByWhitelist.TryGetValue(token, out var dimension))
                return false; // dimensions is still [] here - no partial result on rejection

            if (!resolved.Contains(dimension))
                resolved.Add(dimension);
        }

        dimensions = resolved.ToArray();
        return true;
    }

    /// <summary>Aggregates <paramref name="filtered"/> into one <see cref="LlmUsageTotals"/> via a
    /// single grouped aggregate query (GROUP BY over a constant, so every row folds into one group) -
    /// never N queries and never a client-side fold, because <c>LlmCalls</c> is the largest table in
    /// the database.
    ///
    /// EF's LINQ-to-SQL translation of a nullable <c>Sum</c> is <c>COALESCE(sum(x), 0)</c>, not a bare
    /// <c>sum(x)</c> - it is matching <c>Enumerable.Sum(IEnumerable&lt;long?&gt;)</c>, which returns 0
    /// (not null) over an empty/all-null sequence. Left alone that silently turns "nothing in this set
    /// reported tokens" into "0 tokens", which is exactly the bug this method exists to prevent. So each
    /// token sum is paired with its own per-column "how many rows had a value" count in the same query
    /// (<c>*TokensMeasured</c> locals below), and nulled out in C# when that column's count is zero -
    /// still one round trip, just wider. Those per-column counts are what a future per-column "measured
    /// on N of M" UI qualifier should read; <see cref="LlmUsageTotals.TokenMeasuredCalls"/> answers a
    /// coarser, different question (see its doc comment) and is computed separately
    /// (<c>AnyTokenMeasured</c>) rather than reusing one of the four.</summary>
    public static async Task<LlmUsageTotals> TotalsAsync(IQueryable<LlmCall> filtered, CancellationToken ct)
    {
        var row = await filtered
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Calls = g.Count(),
                Operations = g.Select(c => c.OperationId).Distinct().Count(),
                DurationMs = g.Sum(c => (long)c.DurationMs),
                PromptTokensSum = g.Sum(c => (long?)c.PromptTokens),
                PromptTokensMeasured = g.Count(c => c.PromptTokens != null),
                CompletionTokensSum = g.Sum(c => (long?)c.CompletionTokens),
                CompletionTokensMeasured = g.Count(c => c.CompletionTokens != null),
                ReasoningTokensSum = g.Sum(c => (long?)c.ReasoningTokens),
                ReasoningTokensMeasured = g.Count(c => c.ReasoningTokens != null),
                TotalTokensSum = g.Sum(c => (long?)c.TotalTokens),
                TotalTokensMeasured = g.Count(c => c.TotalTokens != null),
                AnyTokenMeasured = g.Count(c =>
                    c.PromptTokens != null || c.CompletionTokens != null ||
                    c.ReasoningTokens != null || c.TotalTokens != null),
                FailedCalls = g.Count(c => !c.Success),
            })
            .SingleOrDefaultAsync(ct);

        if (row is null)
            return new LlmUsageTotals(0, 0, 0, null, null, null, null, 0, 0, 0, 0, 0, 0, null);

        long? promptTokens = row.PromptTokensMeasured > 0 ? row.PromptTokensSum : null;
        long? completionTokens = row.CompletionTokensMeasured > 0 ? row.CompletionTokensSum : null;
        long? reasoningTokens = row.ReasoningTokensMeasured > 0 ? row.ReasoningTokensSum : null;
        long? totalTokens = row.TotalTokensMeasured > 0 ? row.TotalTokensSum : null;

        // Guard both degenerate cases explicitly: no completion tokens measured, and a zero summed
        // duration. Either would produce NaN/Infinity from a raw division, and both are invalid JSON
        // that would break the page instead of showing "no data".
        double? tokensPerSecond = completionTokens is { } completion && row.DurationMs > 0
            ? completion / (row.DurationMs / 1000.0)
            : null;

        return new LlmUsageTotals(
            row.Calls,
            row.Operations,
            row.DurationMs,
            promptTokens,
            completionTokens,
            reasoningTokens,
            totalTokens,
            row.AnyTokenMeasured,
            row.PromptTokensMeasured,
            row.CompletionTokensMeasured,
            row.ReasoningTokensMeasured,
            row.TotalTokensMeasured,
            row.FailedCalls,
            tokensPerSecond);
    }

    /// <summary>Rolls <paramref name="filtered"/> up into one row per distinct combination of the
    /// requested <paramref name="dimensions"/> - the query behind <c>LlmUsageController.Summary</c>.
    /// Every field NOT in <paramref name="dimensions"/> is folded to a constant before grouping (see the
    /// <c>byUser</c>/<c>byModel</c>/<c>byKind</c> conditionals below), so rows that differ only in an
    /// unrequested dimension collapse into the same group - e.g. <c>groupBy=model</c> alone puts every
    /// user's and every kind's calls for one model into a single row.
    ///
    /// TWO-LEVEL AGGREGATION, not one flat GROUP BY, because "turns" (calls per operation) needs an
    /// AVERAGE and a MAX per operation within the group, never a SUM across operations (see
    /// <see cref="LlmUsageSummaryGroup"/>'s doc comment for why a sum is meaningless here). The first
    /// level groups by (the requested dimensions + OperationId) to get one row per operation *as seen
    /// through this group's dimensions* - Turns, per-operation token sums/measured-counts, duration, and
    /// failed-call count. The second level groups THAT by the requested dimensions alone and aggregates
    /// across operations: Operations = COUNT, Calls = SUM(Turns), AverageTurnsPerOperation = AVG(Turns),
    /// MaxTurnsPerOperation = MAX(Turns), and every token/duration/failure figure summed a second time
    /// (sum-of-sums, which is exact - SUM is associative - as long as the per-operation nulls are folded
    /// to 0 the same way <see cref="TotalsAsync"/> does, with the real "was anything measured" question
    /// answered separately by the *Measured counts, not by the coalesced sum itself).
    ///
    /// This is a single call - two chained <c>GroupBy</c>/<c>Select</c> stages, one <c>ToListAsync</c> at
    /// the end - translated by EF/Npgsql into one SQL statement (an inner GROUP BY subquery feeding an
    /// outer GROUP BY), verified against real Postgres. The result set materialized into memory is bounded
    /// by the number of DISTINCT GROUPS the request can produce, never by the number of matching calls
    /// directly - see <c>LlmUsageController.Summary</c>'s <c>MaxOperationsPerRequest</c> guard (reusing
    /// the same ceiling <c>List</c>'s operations mode uses, on the same quantity: a group can never exist
    /// without at least one operation contributing to it, so the true group count for the widest
    /// <c>groupBy</c> is bounded by the distinct-operation count the same way <c>List</c>'s operations
    /// mode is). Unlike <c>List</c>'s operations mode, nothing here hits the EF/Npgsql compound-ORDER-BY
    /// translation limitation documented there - this method issues no ORDER BY at all.
    ///
    /// <c>UserId</c> IS part of both grouping keys below; <c>UserEmail</c> deliberately is NOT, even
    /// when <paramref name="dimensions"/> includes <see cref="LlmUsageGroupDimension.User"/>. Per
    /// <see cref="LlmCall.UserEmail"/>'s own doc comment, it is an explicitly denormalised SNAPSHOT taken
    /// at call time so a row stays readable after the user is deleted - not an identity field. Two calls
    /// for the SAME <c>UserId</c> could in principle carry two different email snapshots (e.g. the user's
    /// email changed between calls); keying on the pair would silently split one user's usage into two
    /// rows, each looking authoritative - the same class of defect
    /// <c>LlmUsageController.List</c>'s <c>OperationKey</c> comment documents for <c>Model</c>, which cost
    /// two review rounds there. Instead <c>UserEmail</c> is carried as a non-key DISPLAY value via
    /// <c>MAX(UserEmail)</c> at both aggregation levels - an arbitrary but deterministic choice when
    /// snapshots within one group ever disagree, never a second key.</summary>
    public static async Task<IReadOnlyList<LlmUsageSummaryGroup>> SummaryAsync(
        IQueryable<LlmCall> filtered, LlmUsageGroupDimension[] dimensions, CancellationToken ct)
    {
        var byUser = dimensions.Contains(LlmUsageGroupDimension.User);
        var byModel = dimensions.Contains(LlmUsageGroupDimension.Model);
        var byKind = dimensions.Contains(LlmUsageGroupDimension.Kind);

        var perOperation = filtered
            .Select(c => new
            {
                UserId = byUser ? c.UserId : (Guid?)null,
                UserEmail = byUser ? c.UserEmail : null, // display only - NOT in either GroupBy key below
                Model = byModel ? c.Model : null,
                Kind = byKind ? (LlmCallKind?)c.Kind : null,
                c.OperationId,
                c.PromptTokens,
                c.CompletionTokens,
                c.ReasoningTokens,
                c.TotalTokens,
                c.DurationMs,
                c.Success,
            })
            .GroupBy(x => new { x.UserId, x.Model, x.Kind, x.OperationId })
            .Select(g => new
            {
                g.Key.UserId,
                g.Key.Model,
                g.Key.Kind,
                UserEmail = g.Max(x => x.UserEmail),
                Turns = g.Count(),
                PromptTokensSum = g.Sum(x => (long?)x.PromptTokens),
                PromptTokensMeasured = g.Count(x => x.PromptTokens != null),
                CompletionTokensSum = g.Sum(x => (long?)x.CompletionTokens),
                CompletionTokensMeasured = g.Count(x => x.CompletionTokens != null),
                ReasoningTokensSum = g.Sum(x => (long?)x.ReasoningTokens),
                ReasoningTokensMeasured = g.Count(x => x.ReasoningTokens != null),
                TotalTokensSum = g.Sum(x => (long?)x.TotalTokens),
                TotalTokensMeasured = g.Count(x => x.TotalTokens != null),
                AnyTokenMeasured = g.Count(x =>
                    x.PromptTokens != null || x.CompletionTokens != null ||
                    x.ReasoningTokens != null || x.TotalTokens != null),
                DurationMsSum = g.Sum(x => (long)x.DurationMs),
                FailedCalls = g.Count(x => !x.Success),
            });

        var grouped = await perOperation
            .GroupBy(x => new { x.UserId, x.Model, x.Kind })
            .Select(g => new
            {
                g.Key.UserId,
                g.Key.Model,
                g.Key.Kind,
                UserEmail = g.Max(x => x.UserEmail),
                Operations = g.Count(),
                Calls = g.Sum(x => x.Turns),
                AverageTurns = g.Average(x => (double)x.Turns),
                MaxTurns = g.Max(x => x.Turns),
                PromptTokensSum = g.Sum(x => x.PromptTokensSum ?? 0),
                PromptTokensMeasured = g.Sum(x => x.PromptTokensMeasured),
                CompletionTokensSum = g.Sum(x => x.CompletionTokensSum ?? 0),
                CompletionTokensMeasured = g.Sum(x => x.CompletionTokensMeasured),
                ReasoningTokensSum = g.Sum(x => x.ReasoningTokensSum ?? 0),
                ReasoningTokensMeasured = g.Sum(x => x.ReasoningTokensMeasured),
                TotalTokensSum = g.Sum(x => x.TotalTokensSum ?? 0),
                TotalTokensMeasured = g.Sum(x => x.TotalTokensMeasured),
                AnyTokenMeasured = g.Sum(x => x.AnyTokenMeasured),
                DurationMsSum = g.Sum(x => x.DurationMsSum),
                FailedCalls = g.Sum(x => x.FailedCalls),
            })
            .ToListAsync(ct);

        return grouped.Select(x =>
        {
            long? promptTokens = x.PromptTokensMeasured > 0 ? x.PromptTokensSum : null;
            long? completionTokens = x.CompletionTokensMeasured > 0 ? x.CompletionTokensSum : null;
            long? reasoningTokens = x.ReasoningTokensMeasured > 0 ? x.ReasoningTokensSum : null;
            long? totalTokens = x.TotalTokensMeasured > 0 ? x.TotalTokensSum : null;

            // Same two guards as TotalsAsync, applied to THIS group's own sums - never the overall
            // totals' DurationMs/CompletionTokens.
            double? tokensPerSecond = completionTokens is { } completion && x.DurationMsSum > 0
                ? completion / (x.DurationMsSum / 1000.0)
                : null;

            return new LlmUsageSummaryGroup(
                x.UserId, x.UserEmail, x.Model, x.Kind,
                x.Calls, x.Operations, x.AverageTurns, x.MaxTurns,
                promptTokens, completionTokens, reasoningTokens, totalTokens,
                x.AnyTokenMeasured, x.FailedCalls, tokensPerSecond);
        }).ToList();
    }
}
