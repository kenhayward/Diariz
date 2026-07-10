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

    // Recordings are no longer filed under a section directly: the folder is a property of a RoomRecording
    // placement (the folder within a room). See RoomRecording.

    /// <summary>The folder-level LLM summary (a roll-up of the included recordings' summaries), if generated.</summary>
    public SectionSummary? Summary { get; set; }

    /// <summary>The folder-level LLM minutes (the included recordings' minutes reshaped through a template).</summary>
    public SectionMinutes? Minutes { get; set; }

    /// <summary>Attachments filed directly against this folder (not aggregated from its transcripts).</summary>
    public ICollection<SectionAttachment> Attachments { get; set; } = new List<SectionAttachment>();
}
