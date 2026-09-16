using Microsoft.AspNetCore.Identity;

namespace Diariz.Api.Auth;

/// <summary>How many wrong passwords in a row lock an account, and for how long. One place, so the API host and the
/// test host apply the same numbers. Per-account, so it holds however many addresses a guesser spreads across -
/// the per-address rate limits (AuthRateLimits) cover the other direction.</summary>
public static class SignInLockout
{
    public const int MaxFailedAttempts = 10;
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(15);

    public static void Apply(LockoutOptions o)
    {
        o.AllowedForNewUsers = true;
        o.MaxFailedAccessAttempts = MaxFailedAttempts;
        o.DefaultLockoutTimeSpan = Duration;
    }
}
