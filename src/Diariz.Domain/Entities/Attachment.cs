namespace Diariz.Domain.Entities;

/// <summary>What an <see cref="Attachment"/> points at: an uploaded file (stored as a blob) or a URL.</summary>
public enum AttachmentKind
{
    File = 0,
    Url = 1,
}

/// <summary>A supporting document attached to a recording — an uploaded file (PDF, Office doc, email,
/// calendar invite, image, …) stored in object storage, or a URL. Managed by the user (add/rename/remove)
/// and optionally fed to the chat as extra context.</summary>
public class Attachment
{
    public Guid Id { get; set; }
    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    public AttachmentKind Kind { get; set; }

    /// <summary>Display name — the file's name, or the link text for a URL. User-editable.</summary>
    public string Name { get; set; } = string.Empty;

    // ---- File kind ----
    /// <summary>Object-storage key for the uploaded file blob (null for a URL attachment).</summary>
    public string? BlobKey { get; set; }
    /// <summary>MIME type of the uploaded file (null for a URL attachment).</summary>
    public string? ContentType { get; set; }
    /// <summary>Size of the uploaded file in bytes (counts toward the owner's quota; 0 for a URL).</summary>
    public long SizeBytes { get; set; }

    // ---- Url kind ----
    /// <summary>The linked address (http/https) — null for a file attachment.</summary>
    public string? Url { get; set; }

    /// <summary>Sort order within the recording (0-based).</summary>
    public int Ordinal { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
