namespace Diariz.Domain.Entities;

/// <summary>LLM "folder summary" that combines the individual recording summaries across a <see cref="Section"/>
/// and its child sections into one orientation overview. 1:1 with the section. Mirrors <see cref="Summary"/>
/// (which is per-transcription) so the "protect a user edit" logic ports verbatim.</summary>
public class SectionSummary
{
    public Guid Id { get; set; }
    public Guid SectionId { get; set; }
    public Section? Section { get; set; }

    /// <summary>LLM model identifier used to produce the summary (or <see cref="UserEditedModel"/> for a
    /// hand-edited one).</summary>
    public string Model { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The user has edited this summary; the generator then leaves it alone until an explicit
    /// re-generate (which clears this flag first) overwrites it.</summary>
    public bool IsUserEdited { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Generation lifecycle + the last error message (when <see cref="SectionGenerationStatus.Failed"/>),
    /// returned by the API so a client that missed the SignalR event still recovers.</summary>
    public SectionGenerationStatus Status { get; set; } = SectionGenerationStatus.Idle;
    public string? Error { get; set; }

    /// <summary>Sentinel <see cref="Model"/> value for a summary written/edited by the user (no LLM).</summary>
    public const string UserEditedModel = "user";
}
