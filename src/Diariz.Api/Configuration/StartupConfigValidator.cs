using System.Text;

namespace Diariz.Api.Configuration;

/// <summary>What <see cref="StartupConfigValidator"/> found. Errors stop the API starting; warnings are logged.</summary>
public sealed record StartupConfigReport(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>Refuses to start a non-Development API on configuration that would run but should not: signing keys
/// and shared secrets that are missing, too short, or still one of the placeholders shipped in
/// <c>appsettings.json</c> / <c>deploy/.env.example</c>, storage credentials left at the MinIO defaults, no
/// persisted Data Protection keyring, and an OAuth issuer that is not https.
///
/// <para>This lives in the API rather than only in compose because compose is one way to run it among several:
/// a <c>dotnet run</c> with no environment picks up <c>appsettings.json</c>'s placeholders silently, and several
/// of those values are accepted by the libraries that consume them. Some failures are not silent at all but
/// arrive late - an empty JWT key throws on the first authenticated request, not at startup - which is its own
/// reason to check up front.</para>
///
/// <para>Development is exempt so a local run keeps working with the shipped defaults. Messages name the setting
/// and never echo its value.</para></summary>
public static class StartupConfigValidator
{
    private const int MinJwtKeyBytes = 32;
    private const int MinCallbackSecretLength = 16;

    // Fragments that mark a value as a shipped placeholder rather than something an operator chose.
    private static readonly string[] PlaceholderMarkers = ["replace-with", "replace_with", "change-me", "changeme", "change_me"];
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "::1", "[::1]"];

    public static StartupConfigReport Validate(IConfiguration config, bool isDevelopment)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (isDevelopment) return new StartupConfigReport(errors, warnings);

        var jwtKey = config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(jwtKey))
            errors.Add("Jwt:Key is not set. Set JWT_KEY to a random value of at least 32 bytes.");
        else if (IsPlaceholder(jwtKey))
            errors.Add("Jwt:Key is still the shipped placeholder. Set JWT_KEY to a random value of at least 32 bytes.");
        else if (Encoding.UTF8.GetByteCount(jwtKey) < MinJwtKeyBytes)
            errors.Add($"Jwt:Key is shorter than {MinJwtKeyBytes} bytes. Set JWT_KEY to a longer random value.");

        var callback = config["Worker:CallbackSecret"] ?? new WorkerOptions().CallbackSecret;
        if (string.IsNullOrWhiteSpace(callback))
            errors.Add("Worker:CallbackSecret is not set. Set CALLBACK_SECRET (shared with the worker).");
        else if (IsPlaceholder(callback))
            errors.Add("Worker:CallbackSecret is still the shipped placeholder. Set CALLBACK_SECRET to a random value.");
        else if (callback.Length < MinCallbackSecretLength)
            errors.Add($"Worker:CallbackSecret is shorter than {MinCallbackSecretLength} characters. Set CALLBACK_SECRET to a longer random value.");

        var storageDefaults = new StorageOptions();
        CheckStorage(errors, "Storage:AccessKey", config["Storage:AccessKey"] ?? storageDefaults.AccessKey);
        CheckStorage(errors, "Storage:SecretKey", config["Storage:SecretKey"] ?? storageDefaults.SecretKey);

        if (string.IsNullOrWhiteSpace(config["DataProtection:KeysPath"]))
            errors.Add("DataProtection:KeysPath is not set. Without a persisted keyring every stored API key, webhook "
                       + "secret and Google token becomes unreadable when the container is recreated.");

        var seedPassword = config["Seed:Password"];
        if (!string.IsNullOrWhiteSpace(seedPassword) && IsPlaceholder(seedPassword))
            errors.Add("Seed:Password is still the shipped placeholder. Set SEED_PASSWORD to a strong password.");

        CheckIssuer(config, errors, warnings);

        return new StartupConfigReport(errors, warnings);
    }

    private static void CheckStorage(List<string> errors, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{name} is not set. Set the MinIO credentials in .env.");
        else if (string.Equals(value, "minioadmin", StringComparison.OrdinalIgnoreCase) || IsPlaceholder(value))
            errors.Add($"{name} is still a default or placeholder credential. Set the MinIO credentials in .env.");
    }

    private static void CheckIssuer(IConfiguration config, List<string> errors, List<string> warnings)
    {
        var oauthEnabled = config.GetValue("McpOAuth:Enabled", new McpOAuthOptions().Enabled);
        if (!oauthEnabled) return;

        var issuer = config["McpOAuth:Issuer"];
        if (string.IsNullOrWhiteSpace(issuer)) issuer = config["App:PublicUrl"];
        if (string.IsNullOrWhiteSpace(issuer))
        {
            warnings.Add("App:PublicUrl is not set, so the MCP OAuth server has no public issuer. Set APP_PUBLIC_URL "
                         + "to the public https origin, or set MCP_OAUTH_ENABLED=false.");
            return;
        }

        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            errors.Add("The MCP OAuth issuer (McpOAuth:Issuer or App:PublicUrl) is not an absolute http(s) URL.");
            return;
        }
        if (uri.Scheme == Uri.UriSchemeHttps) return;

        if (LoopbackHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            warnings.Add("The MCP OAuth issuer is http on a loopback host. That is fine for a local run, but OAuth "
                         + "clients will only connect over https - set APP_PUBLIC_URL to the public https origin.");
        else
            errors.Add("The MCP OAuth issuer (McpOAuth:Issuer or App:PublicUrl) must be https on a non-loopback host. "
                       + "Set APP_PUBLIC_URL to the public https origin, or set MCP_OAUTH_ENABLED=false.");
    }

    private static bool IsPlaceholder(string value) =>
        PlaceholderMarkers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase));
}
