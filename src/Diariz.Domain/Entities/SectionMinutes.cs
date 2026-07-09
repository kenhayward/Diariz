namespace Diariz.Domain.Entities;

/// <summary>LLM "folder minutes" produced by reshaping the individual recordings' minutes (across a
/// <see cref="Section"/> and its child sections) through a chosen meeting-type template. 1:1 with the section.
/// Mirrors <see cref="MeetingMinutes"/> (which is per-transcription).</summary>
public class SectionMinutes
{
    public Guid Id { get; set; }
    public Guid SectionId { get; set; }
    public Section? Section { get; set; }

    /// <summary>The meeting-type template the folder minutes are reshaped through. Null = the General default.
    /// SetNull if the type is deleted (analogous to <see cref="Recording"/>.<c>MeetingTypeId</c>).</summary>
    public Guid? MeetingTypeId { get; set; }
    public MeetingType? MeetingType { get; set; }

    /// <summary>LLM model identifier used to produce the minutes (or <see cref="UserEditedModel"/> for a
    /// hand-edited one).</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>The minutes as GitHub-flavoured Markdown.</summary>
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The user has edited these minutes; the generator then leaves them alone until an explicit
    /// re-generate (which clears this flag first) overwrites them.</summary>
    public bool IsUserEdited { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public SectionGenerationStatus Status { get; set; } = SectionGenerationStatus.Idle;
    public string? Error { get; set; }

    /// <summary>Sentinel <see cref="Model"/> value for minutes written/edited by the user (no LLM).</summary>
    public const string UserEditedModel = "user";
}
