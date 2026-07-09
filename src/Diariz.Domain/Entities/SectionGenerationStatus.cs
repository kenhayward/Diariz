namespace Diariz.Domain.Entities;

/// <summary>Lifecycle of a folder-level (section) LLM artifact. Sections have no pipeline status of their
/// own (unlike <see cref="Recording"/>.<c>Status</c>), so the folder summary and folder minutes each carry
/// their own generation state. Append only — values persist as ints.</summary>
public enum SectionGenerationStatus
{
    Idle = 0,        // never generated
    Generating = 1,  // a job is in flight
    Ready = 2,       // text present
    Failed = 3       // last generation errored (see Error)
}
