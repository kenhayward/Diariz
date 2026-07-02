using Microsoft.AspNetCore.Identity;

namespace Diariz.Domain.Entities;

/// <summary>Application user. Uses a Guid primary key. All recordings are scoped to a user.</summary>
public class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>Display name, set during account setup. The UI falls back to email when null.</summary>
    public string? FullName { get; set; }

    /// <summary>Access lifecycle. Defaults to Active so registered/seeded users sign in normally;
    /// only the access-request flow creates users as Requested.</summary>
    public UserStatus Status { get; set; } = UserStatus.Active;

    /// <summary>Admin enable/disable switch (independent of <see cref="Status"/>). Disabled users
    /// cannot sign in.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Storage quota in bytes (of recorded audio). Granted at account creation from the
    /// platform starter amount; any administrator may raise it up to the platform maximum.</summary>
    public long QuotaBytes { get; set; } = PlatformSettings.DefaultStarterQuotaBytes;

    /// <summary>Google account subject (the stable <c>sub</c> claim) when the user has linked/sign-in with
    /// Google. Null for password-only accounts. Unique — one Google identity maps to at most one user.</summary>
    public string? GoogleSubject { get; set; }

    /// <summary>Profile picture URL from the linked Google account (refreshed on each Google sign-in).
    /// The account-menu avatar renders this when present, falling back to initials.</summary>
    public string? PictureUrl { get; set; }

    public ICollection<Recording> Recordings { get; set; } = new List<Recording>();
    public UserSettings? Settings { get; set; }
    public ICollection<ChatSession> ChatSessions { get; set; } = new List<ChatSession>();
}
