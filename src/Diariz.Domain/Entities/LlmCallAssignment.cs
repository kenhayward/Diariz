namespace Diariz.Domain.Entities;

/// <summary>Which model serves a call group. At most six rows; a group with no row falls back to
/// <see cref="PlatformSettings.DefaultLlmModelId"/>.</summary>
public class LlmCallAssignment
{
    /// <summary>Primary key. Never <see cref="LlmCallGroup.ModelBase"/> - that is a parameter scope, not a
    /// call type, and the API rejects it.</summary>
    public LlmCallGroup Group { get; set; }

    public Guid LlmModelId { get; set; }
    public LlmModel? Model { get; set; }
}
