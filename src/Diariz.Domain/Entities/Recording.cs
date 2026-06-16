namespace Diariz.Domain.Entities;

/// <summary>A single captured audio recording owned by a user.</summary>
public class Recording
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>Object-storage key (MinIO/S3) for the original audio blob.</summary>
    public string BlobKey { get; set; } = string.Empty;

    /// <summary>MIME / container of the stored audio, e.g. audio/webm, audio/wav.</summary>
    public string ContentType { get; set; } = "audio/webm";

    public long DurationMs { get; set; }
    public RecordingStatus Status { get; set; } = RecordingStatus.Uploaded;
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Transcription> Transcriptions { get; set; } = new List<Transcription>();
    public ICollection<Speaker> Speakers { get; set; } = new List<Speaker>();
}
