namespace Diariz.Domain.Entities;

/// <summary>One scope's parameter set for a model: the model's base set
/// (<see cref="LlmCallGroup.ModelBase"/>) or one call group's override of it.
///
/// Stored as a JSON object where an absent key inherits from the next layer down and a key present with
/// null means "send nothing". Unique per (model, group).</summary>
public class LlmModelParameters
{
    public Guid Id { get; set; }

    public Guid LlmModelId { get; set; }
    public LlmModel? Model { get; set; }

    public LlmCallGroup Group { get; set; }

    /// <summary>jsonb on Postgres, plain text under other providers.</summary>
    public string ParametersJson { get; set; } = "{}";
}
