namespace Diariz.Domain.Entities;

/// <summary>The user's preferred colour theme for the web UI. Stored per-user on <see cref="UserSettings"/>.
/// Append-only (stored as an int) - never renumber. <see cref="Auto"/> follows the OS/browser preference.</summary>
public enum ThemePreference
{
    Auto = 0,
    Light = 1,
    Dark = 2,
}
