using Diariz.Api.Configuration;
using Diariz.Api.Services.Llm;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>Effective embedding config: a server-pinned model/dimension plus an endpoint/key resolved from the
/// AI models page, else the server <c>Embedding</c> block, else whatever model the platform uses for everything
/// else (see <see cref="EmbeddingEndpointSource"/>). Disabled when no endpoint resolves at any level - callers
/// then skip embedding and retrieval stays lexical.
///
/// The model and dimension stay server options rather than joining the platform model rows because the
/// <c>vector(768)</c> column is dimension-pinned: changing them needs a migration and a re-index, which is
/// not something an administrator should be able to do from a settings page.</summary>
public record EmbeddingRequestConfig(
    string ApiBase, string ApiKey, string Model, int Dimension, int TimeoutSeconds, int BatchSize)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiBase);

    /// <summary>Which level supplied <see cref="ApiBase"/>. Shown on the AI models page, where
    /// <see cref="EmbeddingEndpointSource.DefaultModel"/> is a warning: that endpoint moves whenever the
    /// default model does (issue #836).</summary>
    public EmbeddingEndpointSource Source { get; init; } = EmbeddingEndpointSource.None;

    /// <summary>Prefix prepended to a query before embedding (nomic task prefix); empty for models that don't
    /// use them. Applied by the search's semantic arm, not the client.</summary>
    public string QueryPrefix { get; init; } = "";

    /// <summary>Prefix prepended to each chunk before embedding (nomic task prefix); empty for models that don't
    /// use them. Applied by the embedding processor, not the client.</summary>
    public string DocumentPrefix { get; init; } = "";
}

/// <summary>Where the embedding endpoint came from, highest precedence first.</summary>
public enum EmbeddingEndpointSource
{
    /// <summary>Saved on the AI models page (<see cref="PlatformSettings.EmbeddingApiBase"/>).</summary>
    Platform,
    /// <summary>The server's <c>Embedding:ApiBase</c> (<c>EMBED_API_BASE</c>).</summary>
    Server,
    /// <summary>Borrowed from the platform default model, or the environment model when none is configured.</summary>
    DefaultModel,
    /// <summary>Nothing resolved: embedding is off and retrieval stays lexical.</summary>
    None,
}

public interface IEmbeddingSettingsResolver
{
    Task<EmbeddingRequestConfig> ResolveAsync(CancellationToken ct = default);
}

public class EmbeddingSettingsResolver : IEmbeddingSettingsResolver
{
    private readonly DiarizDbContext _db;
    private readonly EmbeddingOptions _emb;
    private readonly ILlmSettingsResolver _llm;
    private readonly IApiKeyProtector? _protector;

    /// <param name="protector">Decrypts a key saved on the AI models page. Optional only so call sites that
    /// never save one (most tests) need not build it; DI always supplies it.</param>
    public EmbeddingSettingsResolver(
        DiarizDbContext db, IOptions<EmbeddingOptions> emb, ILlmSettingsResolver llm,
        IApiKeyProtector? protector = null)
    {
        _db = db;
        _emb = emb.Value;
        _llm = llm;
        _protector = protector;
    }

    public async Task<EmbeddingRequestConfig> ResolveAsync(CancellationToken ct = default)
    {
        // The model + dimension are always the server's (the vector column is dimension-pinned). Only the
        // transport - endpoint, key and deadline - can come from elsewhere.
        //
        // Embedding is a groupless call kind, so the last fallback is the platform default model (or the
        // environment endpoint when none is configured) and never any group's override.
        var platform = await _db.PlatformSettings.AsNoTracking()
            .Where(p => p.Id == PlatformSettings.SingletonId)
            .Select(p => new { p.EmbeddingApiBase, p.EmbeddingApiKeyEncrypted })
            .FirstOrDefaultAsync(ct);

        string apiBase, apiKey;
        int timeoutSeconds = _emb.TimeoutSeconds; // a dedicated endpoint is its own service with its own deadline
        EmbeddingEndpointSource source;
        if (!string.IsNullOrWhiteSpace(platform?.EmbeddingApiBase))
        {
            // Saved on the AI models page. Like the server endpoint below it brings its own key, blank or not.
            apiBase = platform.EmbeddingApiBase.Trim();
            apiKey = _protector?.Unprotect(platform.EmbeddingApiKeyEncrypted) ?? "";
            source = EmbeddingEndpointSource.Platform;
        }
        else if (!string.IsNullOrWhiteSpace(_emb.ApiBase))
        {
            apiBase = _emb.ApiBase.Trim();
            apiKey = _emb.ApiKey;
            source = EmbeddingEndpointSource.Server;
        }
        else
        {
            var fallback = await _llm.ResolveAsync(LlmCallKind.Embedding, ct);
            apiBase = fallback.ApiBase;
            apiKey = fallback.ApiKey;
            // Sharing the platform model's endpoint means sharing its timeout too, or embeddings quietly
            // disagree with every other call to the same server.
            timeoutSeconds = fallback.TimeoutSeconds;
            source = string.IsNullOrWhiteSpace(apiBase) ? EmbeddingEndpointSource.None : EmbeddingEndpointSource.DefaultModel;
        }

        return new EmbeddingRequestConfig(
            ApiBase: apiBase,
            ApiKey: apiKey,
            Model: _emb.Model,
            Dimension: _emb.Dimension,
            TimeoutSeconds: timeoutSeconds,
            BatchSize: Math.Max(1, _emb.BatchSize))
        {
            QueryPrefix = _emb.QueryPrefix ?? "",
            DocumentPrefix = _emb.DocumentPrefix ?? "",
            Source = source,
        };
    }
}
