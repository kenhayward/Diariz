namespace Diariz.Domain.Entities;

/// <summary>Where a recording's audio came from. Append only — values persist as ints.</summary>
public enum RecordingSource
{
    Microphone = 0,
    System = 1,
    Upload = 2,
    Combined = 3 // mic + system audio mixed into one track (single-device dual capture)
}
