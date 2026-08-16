namespace Diariz.Domain.Entities;

/// <summary>One outbound call to a model endpoint. Written by LlmTelemetryHandler via a bounded channel.
///
/// NEVER stores prompt or completion content - counts and sizes only. Meeting content staying out of
/// telemetry is the same rule SentryScrubber enforces, and this table is no exception.
///
/// The user/recording/section links are ON DELETE SET NULL and are paired with a denormalised snapshot
/// (email, title, name) so a row stays readable after its subject is deleted. That is deliberate for an
/// audit trail; the admin's filtered bulk delete is the erasure path.</summary>
public class LlmCall
{
    public Guid Id { get; set; }

    /// <summary>Groups every call made by one user-facing operation. Turns = MAX(Sequence) per operation.</summary>
    public Guid OperationId { get; set; }

    /// <summary>1-based index of this call within its operation.</summary>
    public int Sequence { get; set; }

    public LlmCallKind Kind { get; set; }

    public Guid? UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;

    public Guid? RecordingId { get; set; }
    public string? RecordingTitle { get; set; }

    public Guid? SectionId { get; set; }
    public string? SectionName { get; set; }

    public string Model { get; set; } = string.Empty;

    /// <summary>Scheme, host and path only. The query string is dropped outright rather than scrubbed -
    /// the same rule the handler already applies to span descriptions, because a SignalR JWT reached a
    /// transaction name that way once already.</summary>
    public string Endpoint { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>Stored rather than derived from the two timestamps, so ordering and SUM are trivial.</summary>
    public int DurationMs { get; set; }

    /// <summary>Streaming calls only; null otherwise. Populated in PR 2.</summary>
    public int? TimeToFirstTokenMs { get; set; }

    // All nullable: plenty of OpenAI-compatible servers report no usage at all, and a missing count is
    // not a zero. Aggregates must ignore nulls rather than treat them as 0.
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? ReasoningTokens { get; set; }
    public int? TotalTokens { get; set; }

    /// <summary>Serialized request body length. Shows when LlmContextBudget truncation is biting.</summary>
    public int? PromptChars { get; set; }

    public bool Streamed { get; set; }
    public bool Success { get; set; }
    public int? StatusCode { get; set; }

    /// <summary>A class, never a message body: Timeout, Canceled, Transport, Http500.</summary>
    public string? ErrorKind { get; set; }
}
