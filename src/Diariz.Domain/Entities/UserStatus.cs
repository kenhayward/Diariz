namespace Diariz.Domain.Entities;

/// <summary>Lifecycle of a user account. Persists as an int — append only, never renumber.</summary>
public enum UserStatus
{
    /// <summary>Access requested, awaiting an administrator's grant. No password yet.</summary>
    Requested = 0,
    /// <summary>Granted; a one-time setup link was issued, awaiting the user to set their name + password.</summary>
    Invited = 1,
    /// <summary>Set up and able to sign in.</summary>
    Active = 2,
}
