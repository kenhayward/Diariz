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
