namespace Diariz.Domain.Entities;

/// <summary>LLM-generated summary of a specific transcription version.</summary>
public class Summary
{
    public Guid Id { get; set; }
    public Guid TranscriptionId { get; set; }
    public Transcription? Transcription { get; set; }

    /// <summary>LLM model identifier used to produce the summary.</summary>
    public string Model { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
