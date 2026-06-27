namespace Diariz.Api.Services;

/// <summary>Shared constants/helpers for the one-time account-setup link (issued on grant, consumed
/// on setup). The token is generated and verified via Identity's default token provider.</summary>
public static class AccountSetup
{
    /// <summary>Purpose string for the setup token — distinct so it can't be reused as a password reset.</summary>
    public const string TokenPurpose = "account-setup";

    /// <summary>Builds the front-end setup URL the user opens to finish their account.</summary>
    public static string BuildUrl(string baseUrl, string email, string token) =>
        $"{baseUrl.TrimEnd('/')}/setup?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
}
