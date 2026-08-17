using Diariz.Api.Configuration;
using Diariz.Api.Services.Llm;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

/// <summary>Effective embedding config: a server-pinned model/dimension plus an endpoint/key resolved from
/// the server <c>Embedding</c> block, else whatever model the platform uses for everything else. Disabled
/// when no endpoint resolves at either level - callers then skip embedding and retrieval stays lexical.
///
/// The model and dimension stay server options rather than joining the platform model rows because the
/// <c>vector(768)</c> column is dimension-pinned: changing them needs a migration and a re-index, which is
/// not something an administrator should be able to do from a settings page.</summary>
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
    Task<EmbeddingRequestConfig> ResolveAsync(CancellationToken ct = default);
}

public class EmbeddingSettingsResolver : IEmbeddingSettingsResolver
{
    private readonly EmbeddingOptions _emb;
    private readonly ILlmSettingsResolver _llm;

    public EmbeddingSettingsResolver(
        DiarizDbContext db, IOptions<EmbeddingOptions> emb, ILlmSettingsResolver llm)
    {
        _emb = emb.Value;
        _llm = llm;
    }

    public async Task<EmbeddingRequestConfig> ResolveAsync(CancellationToken ct = default)
    {
        // The model + dimension are always the server's (the vector column is dimension-pinned). Only the
        // transport - endpoint, key and deadline - can come from elsewhere.
        //
        // Embedding is a groupless call kind, so this resolves the platform default model (or the
        // environment endpoint when none is configured) and never any group's override.
        var llm = await _llm.ResolveAsync(LlmCallKind.Embedding, ct);

        var dedicated = !string.IsNullOrWhiteSpace(_emb.ApiBase);

        return new EmbeddingRequestConfig(
            // A dedicated embeddings endpoint wins, and brings its own key even when that key is blank.
            ApiBase: dedicated ? _emb.ApiBase.Trim() : llm.ApiBase,
            ApiKey: dedicated ? _emb.ApiKey : llm.ApiKey,
            Model: _emb.Model,
            Dimension: _emb.Dimension,
            // A dedicated endpoint is its own service and keeps its own deadline; when embeddings share the
            // platform model's endpoint they share its timeout too, or they quietly disagree with every
            // other call to the same server.
            TimeoutSeconds: dedicated ? _emb.TimeoutSeconds : llm.TimeoutSeconds,
            BatchSize: Math.Max(1, _emb.BatchSize))
        {
            QueryPrefix = _emb.QueryPrefix ?? "",
            DocumentPrefix = _emb.DocumentPrefix ?? "",
        };
    }
}
