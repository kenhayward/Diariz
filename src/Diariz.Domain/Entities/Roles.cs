namespace Diariz.Domain.Entities;

/// <summary>The application's role names (account types). Single source of truth shared by the API
/// and tests. PlatformAdministrator is the seed user only — undeletable, undemotable, undisable-able.</summary>
public static class Roles
{
    public const string Standard = "Standard";
    public const string Administrator = "Administrator";
    public const string PlatformAdministrator = "PlatformAdministrator";

    /// <summary>Roles permitted to manage other users.</summary>
    public static readonly string[] Admins = [Administrator, PlatformAdministrator];
}
