namespace Diariz.Domain.Entities;

/// <summary>A supporting document attached directly to a <see cref="Section"/> (folder) - an uploaded file
/// (PDF, Office doc, email, image, ...) stored in object storage, or a URL. Mirrors <see cref="Attachment"/>
/// (which hangs off a recording) but is owned by the folder itself, so it survives independently of any one
/// transcript. Reuses the <see cref="AttachmentKind"/> enum. Cascade-deleted with the section.</summary>
public class SectionAttachment
{
    public Guid Id { get; set; }
    public Guid SectionId { get; set; }
    public Section? Section { get; set; }

    /// <summary>The user who created this attachment - who its bytes are charged to (see
    /// <c>IStorageUsage.UsedBytesAsync</c>). In a shared room, any member with <c>RoomPermission.ManageContents</c>
    /// can add to a folder they don't own, so this can differ from the folder's creator (<see cref="Section.UserId"/>).
    /// Backfilled from <c>Section.UserId</c> for rows created before this column existed.</summary>
    public Guid UploadedByUserId { get; set; }

    public AttachmentKind Kind { get; set; }

    /// <summary>Display name - the file's name, or the link text for a URL. User-editable.</summary>
    public string Name { get; set; } = string.Empty;

    // ---- File kind ----
    /// <summary>Object-storage key for the uploaded file blob (null for a URL attachment).</summary>
    public string? BlobKey { get; set; }
    /// <summary>MIME type of the uploaded file (null for a URL attachment).</summary>
    public string? ContentType { get; set; }
    /// <summary>Size of the uploaded file in bytes (counts toward the owner's quota; 0 for a URL).</summary>
    public long SizeBytes { get; set; }

    // ---- Url kind ----
    /// <summary>The linked address (http/https) - null for a file attachment.</summary>
    public string? Url { get; set; }

    /// <summary>Sort order within the folder (0-based).</summary>
    public int Ordinal { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
