namespace Diariz.Domain.Entities;

/// <summary>An action item extracted from (or hand-added to) a recording's transcript. The user can
/// edit/add/remove these freely; <see cref="Actor"/> and <see cref="Deadline"/> are free text and may be
/// empty. The UI surfaces <see cref="Text"/> as the "Action" column (named Text here to avoid the
/// <c>System.Action</c> identifier clash).</summary>
public class RecordingAction
{
    public Guid Id { get; set; }
    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>The action item itself (the "Action" column).</summary>
    public string Text { get; set; } = string.Empty;
    /// <summary>Who is responsible — free text, may be empty.</summary>
    public string Actor { get; set; } = string.Empty;
    /// <summary>When it's due — free text (e.g. "next Friday", "2026-07-01"), may be empty.</summary>
    public string Deadline { get; set; } = string.Empty;

    /// <summary>Sort order within the recording (0-based).</summary>
    public int Ordinal { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
