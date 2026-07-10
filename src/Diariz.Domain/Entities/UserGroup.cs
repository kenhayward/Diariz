namespace Diariz.Domain.Entities;

/// <summary>A named collection of users carrying platform permissions. Replaces the old Identity roles.
/// A system group (<see cref="IsSystem"/> — the seeded Platform Administrators) cannot be deleted, cannot
/// have its name or permissions edited, and cannot be left without members: otherwise a deployment could
/// end up with nobody able to administer it.</summary>
public class UserGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Icon key from the shared icon set (see the web's MeetingTypeIcon).</summary>
    public string? Icon { get; set; }

    /// <summary>Background colour, hex (e.g. "#5C6BC0").</summary>
    public string? Color { get; set; }

    public PlatformPermission Permissions { get; set; }

    /// <summary>Seeded and protected: the Platform Administrators group.</summary>
    public bool IsSystem { get; set; }

    public ICollection<UserGroupMember> Members { get; set; } = [];
}
