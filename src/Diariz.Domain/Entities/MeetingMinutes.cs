namespace Diariz.Domain.Entities;

/// <summary>LLM-generated meeting minutes for a specific transcription version — a formal, emailable
/// document (GitHub-flavoured Markdown: headings, lists, tables, bold). Sibling of <see cref="Summary"/>,
/// generated in-pipeline and re-creatable/editable on demand.</summary>
public class MeetingMinutes
{
    public Guid Id { get; set; }
    public Guid TranscriptionId { get; set; }
    public Transcription? Transcription { get; set; }

    /// <summary>LLM model identifier used to produce the minutes (or <see cref="UserEditedModel"/> for a
    /// hand-edited one).</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>The minutes as GitHub-flavoured Markdown.</summary>
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The user has edited (or hand-written) these minutes. The automatic generator then leaves them
    /// alone — only an explicit, user-initiated re-create (which clears this flag first) overwrites them.</summary>
    public bool IsUserEdited { get; set; }

    /// <summary>When the minutes were last edited by the user, else null.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Sentinel <see cref="Model"/> value for minutes written/edited by the user (no LLM).</summary>
    public const string UserEditedModel = "user";
}
