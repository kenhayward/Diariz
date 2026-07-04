using Diariz.Api.Configuration;
using Diariz.Domain;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>Effective embedding config for one recording owner: a server-pinned model/dimension plus an
/// endpoint/key resolved from (in order) the server <c>Embedding</c> block, else the owner's own
/// summarisation endpoint, else the server summarisation defaults. Disabled when no endpoint resolves at
/// any level - callers then skip embedding and retrieval stays lexical.</summary>
public record EmbeddingRequestConfig(
    string ApiBase, string ApiKey, string Model, int Dimension, int TimeoutSeconds, int BatchSize)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiBase);

    /// <summary>Prefix prepended to a query before embedding (nomic task prefix); empty for models that don't
    /// use them. Applied by the search's semantic arm, not the client.</summary>
    public string QueryPrefix { get; init; } = "";

    /// <summary>Prefix prepended to each chunk before embedding (nomic task prefix); empty for models that don't
    /// use them. Applied by the embedding processor, not the client.</summary>
    public string DocumentPrefix { get; init; } = "";
}

public interface IEmbeddingSettingsResolver
{
    Task<EmbeddingRequestConfig> ResolveAsync(Guid userId, CancellationToken ct = default);
}

public class EmbeddingSettingsResolver : IEmbeddingSettingsResolver
{
    private readonly DiarizDbContext _db;
    private readonly EmbeddingOptions _emb;
    private readonly SummarizationOptions _summary;
    private readonly IApiKeyProtector _protector;

    public EmbeddingSettingsResolver(
        DiarizDbContext db, IOptions<EmbeddingOptions> emb, IOptions<SummarizationOptions> summary,
        IApiKeyProtector protector)
    {
        _db = db;
        _emb = emb.Value;
        _summary = summary.Value;
        _protector = protector;
    }

    public async Task<EmbeddingRequestConfig> ResolveAsync(Guid userId, CancellationToken ct = default)
    {
        // The model + dimension are always the server's (the vector column is dimension-pinned). Only the
        // transport (endpoint + key) can come from elsewhere.
        string apiBase, apiKey;
        if (!string.IsNullOrWhiteSpace(_emb.ApiBase))
        {
            // A dedicated embeddings endpoint is configured - use it (and its own key, even if blank).
            apiBase = _emb.ApiBase.Trim();
            apiKey = _emb.ApiKey;
        }
        else
        {
            // Reuse the owner's summarisation endpoint (their own value, else the server default).
            var s = await _db.UserSettings.FindAsync([userId], ct);
            apiBase = Coalesce(s?.SummaryApiBase, _summary.ApiBase);
            apiKey = Coalesce(_protector.Unprotect(s?.SummaryApiKeyEncrypted), _summary.ApiKey);
        }

        return new EmbeddingRequestConfig(
            ApiBase: apiBase,
            ApiKey: apiKey,
            Model: _emb.Model,
            Dimension: _emb.Dimension,
            TimeoutSeconds: _emb.TimeoutSeconds,
            BatchSize: Math.Max(1, _emb.BatchSize))
        {
            QueryPrefix = _emb.QueryPrefix ?? "",
            DocumentPrefix = _emb.DocumentPrefix ?? "",
        };
    }

    private static string Coalesce(string? user, string server) =>
        string.IsNullOrWhiteSpace(user) ? server : user.Trim();
}
