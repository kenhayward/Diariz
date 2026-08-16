using Diariz.Api.Contracts;
using Diariz.Domain.Entities;

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
}
