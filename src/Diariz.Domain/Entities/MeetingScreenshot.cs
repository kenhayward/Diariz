namespace Diariz.Domain.Entities;

/// <summary>A screen capture taken during a recording from the desktop client. <see cref="CapturedAtMs"/>
/// is the offset into the *recorded* clock (pause-aware, stamped by the recorder) - an immutable capture
/// fact, which is why it is non-nullable here even though <see cref="MeetingNote.CapturedAtMs"/> is not.
/// Two blobs are stored per capture: the full PNG and a small JPEG thumbnail for inline rendering.</summary>
public class MeetingScreenshot
{
    public Guid Id { get; set; }

    /// <summary>Owner - the recording's owner at capture time.</summary>
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>Offset (ms) into the recording clock. Not user-editable.</summary>
    public long CapturedAtMs { get; set; }

    /// <summary>Object-storage key for the full PNG.</summary>
    public string BlobKey { get; set; } = string.Empty;
    /// <summary>Object-storage key for the JPEG thumbnail.</summary>
    public string ThumbBlobKey { get; set; } = string.Empty;

    /// <summary>Pixel dimensions of the full image.</summary>
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Full plus thumbnail bytes; counts toward the owner's storage quota.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Sort order within the recording (0-based).</summary>
    public int Ordinal { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
