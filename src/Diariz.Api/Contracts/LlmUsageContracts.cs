using Diariz.Domain.Entities;

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

// ---- LLM usage viewer rows (LlmUsageController) ----

/// <summary>One <c>LlmCalls</c> row, as returned by <c>mode=calls</c>.</summary>
public record LlmUsageCallRow(
    Guid Id,
    Guid OperationId,
    int Sequence,
    LlmCallKind Kind,
    Guid? UserId,
    string UserEmail,
    Guid? RecordingId,
    string? RecordingTitle,
    Guid? SectionId,
    string? SectionName,
    string Model,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int DurationMs,
    int? PromptTokens,
    int? CompletionTokens,
    int? ReasoningTokens,
    int? TotalTokens,
    bool Success,
    int? StatusCode,
    string? ErrorKind);

/// <summary>One operation - every <c>LlmCalls</c> row sharing an <c>OperationId</c>, collapsed to a
/// single row, as returned by <c>mode=operations</c> (the default). See
/// <c>LlmUsageController.List</c> for why grouping by the composite key below is safe.</summary>
public record LlmUsageOperationRow(
    Guid OperationId,
    LlmCallKind Kind,
    Guid? UserId,
    string UserEmail,
    Guid? RecordingId,
    string? RecordingTitle,
    Guid? SectionId,
    string? SectionName,
    string Model,
    int Turns,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    long? PromptTokens,
    long? CompletionTokens,
    long? ReasoningTokens,
    long? TotalTokens,
    bool Success);

/// <summary>One page of <typeparamref name="TRow"/>, plus totals over the WHOLE filtered set (not the
/// page - see <see cref="LlmUsageTotals"/>) and <paramref name="Total"/>, the row/operation count of the
/// whole filtered set before paging. Neither <paramref name="Total"/> nor <paramref name="Totals"/> may
/// be derived from <paramref name="Rows"/>.</summary>
public record LlmUsagePage<TRow>(
    IReadOnlyList<TRow> Rows,
    int Page,
    int PageSize,
    int Total,
    LlmUsageTotals Totals);

// ---- LLM usage viewer summary (LlmUsageController.Summary - the roll-up view) ----

/// <summary>One roll-up row from <c>GET /api/admin/llm-usage/summary</c> - <c>LlmCalls</c> collapsed by
/// whichever of <c>user</c>/<c>model</c>/<c>kind</c> the caller asked to group by (see
/// <c>LlmUsageQuery.TryResolveGroupBy</c>). A dimension NOT requested is null on every row (every row
/// with a different value for that dimension folds into the same group); a dimension that IS requested
/// is never null on a real row (its column value, or the denormalised email snapshot for
/// <paramref name="UserEmail"/>).
///
/// <paramref name="AverageTurnsPerOperation"/>/<paramref name="MaxTurnsPerOperation"/> are computed PER
/// OPERATION (an operation's "turns" is how many calls it made), never summed across the operations in
/// the group - see <c>LlmUsageQuery.SummaryAsync</c>'s two-level aggregation. Summing turns across
/// operations is a meaningless number that looks authoritative: two operations of 2 and 5 turns is "up
/// to 5 turns per operation, 3.5 on average" - not "7 turns".
///
/// <paramref name="TokensPerSecond"/> is this GROUP's own <c>SUM(CompletionTokens) / SUM(DurationMs)</c>
/// - never an average of its rows' own rates (one tiny fast call must not outweigh one huge slow one),
/// and never the overall <see cref="LlmUsageTotals.TokensPerSecond"/> inherited unchanged. Null (never
/// NaN/Infinity, which are not valid JSON) under the same two guards as
/// <see cref="LlmUsageTotals.TokensPerSecond"/>: no completion tokens measured in the group, or the
/// group's summed duration is zero.</summary>
public record LlmUsageSummaryGroup(
    Guid? UserId,
    string? UserEmail,
    string? Model,
    LlmCallKind? Kind,
    int Calls,
    int Operations,
    double AverageTurnsPerOperation,
    int MaxTurnsPerOperation,
    long? PromptTokens,
    long? CompletionTokens,
    long? ReasoningTokens,
    long? TotalTokens,
    int TokenMeasuredCalls,
    int FailedCalls,
    double? TokensPerSecond);

/// <summary>Response for <c>GET /api/admin/llm-usage/summary</c>: one row per requested group, plus
/// <see cref="LlmUsageTotals"/> over the WHOLE filtered set - computed by the same
/// <c>LlmUsageQuery.TotalsAsync</c> the detail view uses, over a filter built by the same
/// <c>LlmUsageQuery.Apply</c>, so the summary and the detail view can never silently disagree about what
/// is in scope. <see cref="Totals"/> is never derived from folding over <see cref="Groups"/>.</summary>
public record LlmUsageSummary(IReadOnlyList<LlmUsageSummaryGroup> Groups, LlmUsageTotals Totals);

// ---- LLM usage viewer delete (LlmUsageController.Delete) ----

/// <summary>Response for <c>DELETE /api/admin/llm-usage</c> - the count of rows permanently removed.
/// There is no way to recover them: the usage log is the only record a given LLM call ever happened.</summary>
public record LlmUsageDeleteResult(int Deleted);

// ---- LLM usage viewer filter options (LlmUsageController.Filters) ----

/// <summary>One user with at least one <c>LlmCalls</c> row in the scoped set, for populating the
/// viewer's user filter dropdown. <paramref name="UserEmail"/> is the most recent denormalised snapshot
/// in scope (<c>MAX(UserEmail)</c>) - the same "arbitrary but deterministic" convention
/// <c>LlmUsageQuery.SummaryAsync</c> uses when a user's email snapshot disagrees across rows (see its own
/// doc comment on why <c>UserEmail</c> is a snapshot, never an identity field).</summary>
public record LlmUsageFilterUser(Guid UserId, string UserEmail);

/// <summary>Response for <c>GET /api/admin/llm-usage/filters</c>: the distinct users, models and kinds
/// that actually occur in the scoped set - never every enum value or every user in the system - so the
/// viewer's filter dropdowns can never offer a combination guaranteed to match zero rows. Scoped through
/// <c>LlmUsageQuery.Apply</c> with only <c>From</c>/<c>To</c> set (every other field null/"no filter") -
/// see <c>LlmUsageController.Filters</c> for why it respects the same default 30-day window as every
/// other endpoint rather than reporting across all history.</summary>
public record LlmUsageFilterOptions(
    IReadOnlyList<LlmUsageFilterUser> Users,
    IReadOnlyList<string> Models,
    IReadOnlyList<LlmCallKind> Kinds);
