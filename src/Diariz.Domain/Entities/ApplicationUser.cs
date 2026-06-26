using Microsoft.AspNetCore.Identity;

namespace Diariz.Domain.Entities;

/// <summary>Application user. Uses a Guid primary key. All recordings are scoped to a user.</summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public ICollection<Recording> Recordings { get; set; } = new List<Recording>();
    public UserSettings? Settings { get; set; }
}
