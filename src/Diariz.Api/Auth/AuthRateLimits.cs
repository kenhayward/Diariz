using System.Threading.RateLimiting;

namespace Diariz.Api.Auth;

/// <summary>Request budgets for the anonymous sign-in and OAuth routes (<c>RateLimits</c> config section).</summary>
public sealed class AuthRateLimitOptions
{
    public const string Section = "RateLimits";

    /// <summary>Master switch. On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Password sign-in, access requests, account setup, and the Google/desktop sign-in hand-offs.</summary>
    public int SignInPerMinute { get; set; } = 20;

    /// <summary><c>/connect/authorize</c> and <c>/connect/token</c>. Higher: a connector refreshes on its own.</summary>
    public int OAuthPerMinute { get; set; } = 60;

    /// <summary>Dynamic client registration. Every call adds a row, so it gets an hourly budget of its own.</summary>
    public int RegistrationsPerHour { get; set; } = 20;
}

/// <summary>Per-address budgets on the routes that accept a request from anyone: password sign-in and its
/// neighbours, and the OAuth authorization server. The per-account lockout (<see cref="SignInLockout"/>) stops one
/// account being guessed at from many addresses; this stops one address guessing across many accounts, or
/// flooding client registration.
///
/// <para>A single global limiter keyed on the path, not endpoint attributes, because <c>/connect/token</c> is
/// answered by OpenIddict's middleware and never reaches an endpoint. It must run after
/// <c>UseForwardedHeaders</c> (so the address is the client's, not the proxy's) and before
/// <c>UseAuthentication</c> (where OpenIddict handles the token request).</para></summary>
public static class AuthRateLimits
{
    public const string SignIn = "sign-in";
    public const string OAuth = "oauth";
    public const string Registration = "registration";

    private static readonly (string Method, string Path, string Budget)[] Routes =
    [
        ("POST", "/api/auth/login", SignIn),
        ("POST", "/api/auth/request-access", SignIn),
        ("GET", "/api/auth/setup/validate", SignIn),
        ("POST", "/api/auth/setup", SignIn),
        ("GET", "/api/auth/google/start", SignIn),
        ("GET", "/api/auth/google/callback", SignIn),
        ("POST", "/api/auth/google/exchange", SignIn),
        ("POST", "/api/auth/desktop/exchange", SignIn),
        ("GET", "/connect/authorize", OAuth),
        ("POST", "/connect/authorize", OAuth),
        ("POST", "/connect/token", OAuth),
        ("POST", "/connect/register", Registration),
    ];

    /// <summary>The budget a request draws from, or null when the route is not limited.</summary>
    public static string? BudgetFor(HttpContext http)
    {
        var path = (http.Request.Path.Value ?? "").TrimEnd('/');
        foreach (var (method, route, budget) in Routes)
            if (string.Equals(http.Request.Method, method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(path, route, StringComparison.OrdinalIgnoreCase))
                return budget;
        return null;
    }

    public static PartitionedRateLimiter<HttpContext> CreateLimiter(AuthRateLimitOptions options) =>
        PartitionedRateLimiter.Create<HttpContext, string>(http =>
        {
            var budget = options.Enabled ? BudgetFor(http) : null;
            if (budget is null) return RateLimitPartition.GetNoLimiter("unlimited");

            var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var (permits, window) = budget switch
            {
                Registration => (options.RegistrationsPerHour, TimeSpan.FromHours(1)),
                OAuth => (options.OAuthPerMinute, TimeSpan.FromMinutes(1)),
                _ => (options.SignInPerMinute, TimeSpan.FromMinutes(1)),
            };
            return RateLimitPartition.GetFixedWindowLimiter($"{budget}|{address}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, permits),
                Window = window,
                QueueLimit = 0,
            });
        });
}
