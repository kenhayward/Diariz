using Diariz.Api.Configuration;
using Diariz.Domain;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>Effective summarisation config for one request: the user's own value per field, falling
/// back to the server defaults. Disabled when no endpoint is configured at either level.</summary>
public record SummarizationRequestConfig(string ApiBase, string ApiKey, string Model, int TimeoutSeconds)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiBase);

    /// <summary>The <c>reasoning_effort</c> to send on requests, or null to omit the field entirely (the
    /// resolver leaves it null when reasoning is off, so clients never send it to non-reasoning endpoints).</summary>
    public string? ReasoningEffort { get; init; }
}

public interface ISummarizationSettingsResolver
{
    Task<SummarizationRequestConfig> ResolveAsync(Guid userId, CancellationToken ct = default);
}

public class SummarizationSettingsResolver : ISummarizationSettingsResolver
{
    private readonly DiarizDbContext _db;
    private readonly SummarizationOptions _opts;
    private readonly IApiKeyProtector _protector;

    public SummarizationSettingsResolver(
        DiarizDbContext db, IOptions<SummarizationOptions> opts, IApiKeyProtector protector)
    {
        _db = db;
        _opts = opts.Value;
        _protector = protector;
    }

    public async Task<SummarizationRequestConfig> ResolveAsync(Guid userId, CancellationToken ct = default)
    {
        var s = await _db.UserSettings.FindAsync([userId], ct);

        // Reasoning: user override ?? server default. When on, the effort is the user's value ?? the
        // server's; when off, leave it null so clients omit the param entirely.
        var reasoningOn = s?.ReasoningEnabled ?? _opts.ReasoningEnabled;
        var effort = reasoningOn ? Coalesce(s?.ReasoningEffort, _opts.ReasoningEffort) : null;

        return new SummarizationRequestConfig(
            ApiBase: Coalesce(s?.SummaryApiBase, _opts.ApiBase),
            ApiKey: Coalesce(_protector.Unprotect(s?.SummaryApiKeyEncrypted), _opts.ApiKey),
            Model: Coalesce(s?.SummaryModel, _opts.Model),
            TimeoutSeconds: _opts.TimeoutSeconds)
        {
            ReasoningEffort = effort,
        };
    }

    private static string Coalesce(string? user, string server) =>
        string.IsNullOrWhiteSpace(user) ? server : user.Trim();
}
