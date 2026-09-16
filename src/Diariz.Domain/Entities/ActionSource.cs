namespace Diariz.Domain.Entities;

/// <summary>Where an action item came from. Stored as an int: append only, never renumber.</summary>
public enum ActionSource
{
    /// <summary>Found by the LLM extraction (the default, and what every row predating this column was).</summary>
    Extracted = 0,
    /// <summary>Added by hand on the recording's page or through the API.</summary>
    Manual = 1,
    /// <summary>Typed into the live notes panel while the meeting was being recorded.</summary>
    Live = 2,
}
