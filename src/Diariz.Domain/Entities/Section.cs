namespace Diariz.Domain.Entities;

/// <summary>A user-defined group that recordings can be filed under in the list. Recordings with a
/// null SectionId are "Ungrouped".</summary>
public class Section
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Recording> Recordings { get; set; } = new List<Recording>();
}
