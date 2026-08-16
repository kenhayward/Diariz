namespace Diariz.Api.Contracts;

// ---- LLM usage viewer (Platform Administrator) ----

/// <summary>Filter for the LLM usage log. Every endpoint over <c>LlmCalls</c> shares this shape via
/// <c>LlmUsageQuery.Apply</c>, so the filtering rules live in exactly one place.
///
/// <paramref name="From"/> null defaults to 30 days before "now" (see <c>LlmUsageQuery.Apply</c>) -
/// <c>LlmCalls</c> is the largest table in the database, so an unbounded default would make the
/// viewer's first page a full scan. <paramref name="UserIds"/>/<paramref name="Kinds"/>/
/// <paramref name="Models"/> null OR empty both mean "no filter on this dimension" - a UI multi-select
/// that has been cleared sends an empty array, and that must not be read as "match nothing".
/// <paramref name="Outcome"/> is <c>"ok"</c> | <c>"failed"</c> | <c>"all"</c>/null.</summary>
public record LlmUsageFilter(
    DateTimeOffset? From,
    DateTimeOffset? To,
    Guid[]? UserIds,
    int[]? Kinds,
    string[]? Models,
    string? Outcome,
    Guid? RecordingId,
    Guid? SectionId);

/// <summary>Aggregate totals over a filtered <c>LlmCalls</c> query, computed by
/// <c>LlmUsageQuery.TotalsAsync</c> in a single grouped aggregate query (never a per-row fold in
/// .NET - <c>LlmCalls</c> is the largest table in the database).
///
/// <paramref name="Operations"/> is <c>COUNT(DISTINCT OperationId)</c>, not <c>COUNT(*)</c> - a
/// multi-round-trip operation (e.g. a chat turn) must count once. The token sums are all
/// <c>long?</c> because <c>SUM</c> over an all-null/empty set is SQL NULL, never 0 - a model that
/// reports no usage must not silently read as "emitted zero tokens".
///
/// <paramref name="TokenMeasuredCalls"/> is how many calls in the set reported ANY token count at
/// all - prompt, completion, reasoning, or total (any one of the four being non-null counts the
/// call). It answers "how many of these calls do we have usage data for at all", which an
/// administrator needs before trusting any total on the page - it is deliberately NOT scoped to
/// <see cref="Diariz.Domain.Entities.LlmCall.CompletionTokens"/> specifically, because that would
/// silently duplicate one column's own measured-count under a name that implies something broader.
/// If the UI ever needs a *per-column* "measured on N of M" qualifier (e.g. specifically for
/// completion tokens), that is a different, narrower question than this field answers and needs its
/// own count - <c>LlmUsageQuery.TotalsAsync</c> already computes one such count per token column
/// internally (to null out that column's sum correctly) but does not expose them individually here.
/// <paramref name="TokensPerSecond"/> is <c>SUM(CompletionTokens) / SUM(DurationMs)</c> across the
/// whole set - never an average of each row's own rate, which lets one tiny fast call outweigh one
/// huge slow one. It is null (never NaN/Infinity, which are not valid JSON) when no completion
/// tokens were measured or the summed duration is zero.</summary>
public record LlmUsageTotals(
    int Calls,
    int Operations,
    long DurationMs,
    long? PromptTokens,
    long? CompletionTokens,
    long? ReasoningTokens,
    long? TotalTokens,
    int TokenMeasuredCalls,
    int FailedCalls,
    double? TokensPerSecond);
