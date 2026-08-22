namespace Diariz.Domain.Entities;

/// <summary>The scope a parameter set applies to: a model's base set, or one call group's override of it.
///
/// <see cref="ModelBase"/> is a real member rather than a null marker because it forms half of a unique
/// index with the model id, and Postgres treats NULLs as distinct - a nullable "this is the base" column
/// would silently permit two base rows per model.
///
/// Append only, never renumber: these are ints in Postgres, the same rule as RecordingSource.</summary>
public enum LlmCallGroup
{
    ModelBase = 0,
    Tags = 1,
    Actions = 2,
    Summaries = 3,
    MinutesAndFormulas = 4,
    Translation = 5,
    Chat = 6,
    Ocr = 7,
}
