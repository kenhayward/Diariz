namespace Diariz.Domain.Entities;

/// <summary>How template-driven meeting minutes are generated. Platform-wide, chosen by a Platform Administrator;
/// takes effect from the next template run. Append-only (stored as an int) - never renumber.</summary>
public enum MinutesGenerationMode
{
    /// <summary>Render the whole template into a single prompt and make one LLM call (token-frugal; the default).</summary>
    SingleCall = 0,

    /// <summary>Run one LLM call per model-prompt block (guaranteed structure, higher per-section fidelity).</summary>
    PerSection = 1,
}
