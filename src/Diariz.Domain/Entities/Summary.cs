namespace Diariz.Domain.Entities;

/// <summary>LLM-generated summary of a specific transcription version.</summary>
public class Summary
{
    public Guid Id { get; set; }
    public Guid TranscriptionId { get; set; }
    public Transcription? Transcription { get; set; }

    /// <summary>LLM model identifier used to produce the summary (or <see cref="UserEditedModel"/> for a
    /// hand-written/edited one).</summary>
    public string Model { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The user has edited (or hand-written) this summary. The automatic summariser then leaves
    /// it alone — only an explicit, user-initiated re-summarise (which clears this flag first) overwrites it.</summary>
    public bool IsUserEdited { get; set; }

    /// <summary>When the summary was last edited by the user, else null.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Sentinel <see cref="Model"/> value for a summary written/edited by the user (no LLM).</summary>
    public const string UserEditedModel = "user";
}
