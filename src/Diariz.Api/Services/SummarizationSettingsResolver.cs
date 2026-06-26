using Diariz.Api.Configuration;
using Diariz.Domain;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>Effective summarisation config for one request: the user's own value per field, falling
/// back to the server defaults. Disabled when no endpoint is configured at either level.</summary>
public record SummarizationRequestConfig(string ApiBase, string ApiKey, string Model, int TimeoutSeconds)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiBase);
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
        return new SummarizationRequestConfig(
            ApiBase: Coalesce(s?.SummaryApiBase, _opts.ApiBase),
            ApiKey: Coalesce(_protector.Unprotect(s?.SummaryApiKeyEncrypted), _opts.ApiKey),
            Model: Coalesce(s?.SummaryModel, _opts.Model),
            TimeoutSeconds: _opts.TimeoutSeconds);
    }

    private static string Coalesce(string? user, string server) =>
        string.IsNullOrWhiteSpace(user) ? server : user.Trim();
}
