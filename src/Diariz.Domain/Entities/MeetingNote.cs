namespace Diariz.Domain.Entities;

/// <summary>One line of the user's own meeting notes - a sparse trigger phrase, question, or observation.
/// Anchored to EITHER a recording (RecordingId set) OR an upcoming calendar event (CalendarId+EventId set,
/// RecordingId null); when a recording links to that event, event-anchored lines are adopted onto the
/// recording (RecordingId set, event keys cleared). <see cref="CapturedAtMs"/> is the offset into the
/// *recorded* clock (pause-aware, stamped by the live notes panel) - an immutable capture fact; null for
/// pre-meeting or post-hoc lines. Feeds minutes generation (steering + the Enhanced notes section).</summary>
public class MeetingNote
{
    public Guid Id { get; set; }

    /// <summary>Owner. Event-anchored notes have no recording, so ownership hangs off the user directly.</summary>
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public Guid? RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>Pre-meeting anchor (with <see cref="EventId"/>); cleared when adopted onto a recording.</summary>
    public string? CalendarId { get; set; }
    public string? EventId { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Offset (ms) into the recording clock; null = pre-meeting/post-hoc. Not user-editable.</summary>
    public long? CapturedAtMs { get; set; }

    /// <summary>Sort order within the anchor (0-based).</summary>
    public int Ordinal { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
