namespace Diariz.Domain.Entities;

/// <summary>Where a newly recorded audio lands within the recorder's Personal room. Append-only (stored as an
/// int) - never renumber.
/// <list type="bullet">
/// <item><see cref="Ungrouped"/> - always file it under no folder.</item>
/// <item><see cref="SelectedFolder"/> - the folder the user had selected when they pressed Record (the
/// default).</item>
/// <item><see cref="SpecificFolder"/> - a fixed folder the user chose in settings
/// (<see cref="UserSettings.RecordingPlacementSectionId"/>).</item>
/// </list></summary>
public enum RecordingPlacementMode
{
    Ungrouped = 0,
    SelectedFolder = 1,
    SpecificFolder = 2,
}
