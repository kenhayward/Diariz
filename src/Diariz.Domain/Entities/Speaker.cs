namespace Diariz.Domain.Entities;

/// <summary>
/// Maps a diarization label (e.g. "SPEAKER_00") to a user-supplied display name
/// for a given recording. Lets the user rename "Speaker 1" to "Alice".
/// </summary>
public class Speaker
{
    public Guid Id { get; set; }
    public Guid RecordingId { get; set; }
    public Recording? Recording { get; set; }

    /// <summary>The raw diarization label this mapping applies to.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>User-facing display name.</summary>
    public string DisplayName { get; set; } = string.Empty;
}
