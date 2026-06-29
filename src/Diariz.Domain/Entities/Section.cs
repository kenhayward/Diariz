namespace Diariz.Domain.Entities;

/// <summary>A user-defined group that recordings can be filed under in the list. Recordings with a
/// null SectionId are "Ungrouped".</summary>
public class Section
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Optional parent section, for one level of nesting (e.g. "Customers" › "Acme Corp").
    /// Null = a top-level section. The hierarchy is capped at two levels: a section that has a
    /// <see cref="ParentId"/> can't itself be a parent (enforced in the controller).</summary>
    public Guid? ParentId { get; set; }
    public Section? Parent { get; set; }
    public ICollection<Section> Children { get; set; } = new List<Section>();

    /// <summary>Manual sort order among its siblings (lower = higher in the list). Replaces the old
    /// alphabetical ordering so sections/sub-sections can be drag-reordered.</summary>
    public int Position { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Recording> Recordings { get; set; } = new List<Recording>();
}
