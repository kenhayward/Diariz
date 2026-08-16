using Diariz.Api.Contracts;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services;

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
            source = source.Where(c => kinds.Contains((int)c.Kind));

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
            return new LlmUsageTotals(0, 0, 0, null, null, null, null, 0, 0, null);

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
            row.FailedCalls,
            tokensPerSecond);
    }
}
