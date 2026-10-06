using System.Diagnostics;
using System.Security.Claims;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Diariz.Api.Services;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Controllers;

/// <summary>The embedding card on the AI models page (issue #836).
///
/// <para>Embeddings used to follow the default model's endpoint whenever no <c>EMBED_API_BASE</c> was set, so
/// changing the chat model moved them to a server that could not embed: every call 404'd, indexing stopped, and
/// nothing on any page said so. This shows where they actually go, lets an administrator save an endpoint that
/// wins over both, and tests it - including the vector size, since a server running the wrong embedding model
/// answers 200 and then fails every insert into the dimension-pinned column.</para>
///
/// <para>Only the transport is editable. The model and dimension stay server options: changing them needs a
/// migration and a re-index, which no settings page should be able to start.</para></summary>
[ApiController]
[Authorize(Policy = "ManagePlatform")]
[Route("api/admin/embedding")]
public class EmbeddingSettingsController : ControllerBase
{
    /// <summary>What the test embeds. Short, so the test measures reachability rather than throughput.</summary>
    internal const string TestSample = "Diariz embedding test";

    private readonly DiarizDbContext _db;
    private readonly IApiKeyProtector _protector;
    private readonly EmbeddingOptions _emb;
    private readonly IEmbeddingSettingsResolver _resolver;
    private readonly IEmbeddingClient _client;
    private readonly IJobQueue _queue;
    private readonly ILogger<EmbeddingSettingsController> _logger;

    public EmbeddingSettingsController(
        DiarizDbContext db, IApiKeyProtector protector, IOptions<EmbeddingOptions> emb,
        IEmbeddingSettingsResolver resolver, IEmbeddingClient client, IJobQueue queue,
        ILogger<EmbeddingSettingsController> logger)
    {
        _db = db;
        _protector = protector;
        _emb = emb.Value;
        _resolver = resolver;
        _client = client;
        _queue = queue;
        _logger = logger;
    }

    [HttpGet]
    [EndpointSummary("Show where embeddings are sent")]
    public async Task<EmbeddingSettingsDto> Get() => await ToDtoAsync(HttpContext?.RequestAborted ?? default);

    [HttpPut]
    [EndpointSummary("Save the embedding endpoint")]
    [EndpointDescription(
        "A blank apiBase clears the saved endpoint and its key, handing control back to EMBED_API_BASE or the " +
        "default model. apiKey: null keeps the saved key, empty clears it. Every recording with no index is " +
        "queued for embedding afterwards, so a corrected endpoint catches up without a restart.")]
    public async Task<ActionResult<EmbeddingSettingsSaveResult>> Update(UpdateEmbeddingSettingsRequest req)
    {
        var ct = HttpContext?.RequestAborted ?? default;
        var apiBase = req.ApiBase?.Trim();
        if (!string.IsNullOrEmpty(apiBase)
            && !(Uri.TryCreate(apiBase, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
            return BadRequest("The embedding endpoint must be an absolute http:// or https:// URL.");

        var row = await _db.PlatformSettings.FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId, ct);
        if (row is null)
        {
            row = new PlatformSettings { Id = PlatformSettings.SingletonId };
            _db.PlatformSettings.Add(row);
        }

        if (string.IsNullOrEmpty(apiBase))
        {
            // The key goes with the endpoint: kept, it would be sent to whatever endpoint is saved next.
            row.EmbeddingApiBase = null;
            row.EmbeddingApiKeyEncrypted = null;
        }
        else
        {
            row.EmbeddingApiBase = apiBase;
            if (req.ApiKey is not null) row.EmbeddingApiKeyEncrypted = _protector.Protect(req.ApiKey);
        }
        await _db.SaveChangesAsync(ct);

        // Recordings transcribed while the endpoint was wrong have no index, and the startup backfill only runs
        // on a restart. Idempotent - indexed recordings are skipped - so a no-op save costs one query.
        var queued = 0;
        if ((await _resolver.ResolveAsync(ct)).Enabled)
            queued = await EmbeddingBackfill.RunAsync(_db, _queue, _logger, ct);

        return Ok(new EmbeddingSettingsSaveResult(await ToDtoAsync(ct), queued));
    }

    /// <summary>Embeds one short sample against the effective endpoint. Scoped as
    /// <see cref="LlmCallKind.AdminTest"/> so it lands in the usage log, like the model tests, rather than among
    /// the real embedding calls it would otherwise be indistinguishable from.</summary>
    [HttpPost("test")]
    [EndpointSummary("Test the embedding endpoint")]
    public async Task<EmbeddingTestResult> Test()
    {
        var ct = HttpContext?.RequestAborted ?? default;
        var config = await _resolver.ResolveAsync(ct);
        if (!config.Enabled)
            return new EmbeddingTestResult(false, null, config.Model, config.Dimension, null, null,
                "No embedding endpoint is configured, so search is keyword-only.", 0);

        var userId = CallerId;
        var userEmail = userId is null
            ? null
            : await _db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        using var scope = LlmCallScope.Push(LlmCallKind.AdminTest, userId, userEmail);

        var clock = Stopwatch.StartNew();
        try
        {
            var vectors = await _client.EmbedAsync(config, [TestSample], ct);
            var dimension = vectors.Count > 0 ? vectors[0]?.Length : null;
            var ok = dimension == config.Dimension;
            return new EmbeddingTestResult(
                ok, config.ApiBase, config.Model, config.Dimension, dimension, 200,
                ok ? null : $"The endpoint returned a {dimension ?? 0}-dimension vector, but the index stores " +
                            $"{config.Dimension}. It is probably serving a different embedding model than '{config.Model}'.",
                clock.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            return new EmbeddingTestResult(
                false, config.ApiBase, config.Model, config.Dimension, null,
                e is HttpRequestException { StatusCode: { } status } ? (int)status : null,
                e is TaskCanceledException ? $"No reply within {config.TimeoutSeconds}s." : e.Message,
                clock.ElapsedMilliseconds);
        }
    }

    private async Task<EmbeddingSettingsDto> ToDtoAsync(CancellationToken ct)
    {
        var config = await _resolver.ResolveAsync(ct);
        var saved = await _db.PlatformSettings.AsNoTracking()
            .Where(p => p.Id == PlatformSettings.SingletonId)
            .Select(p => new { p.EmbeddingApiBase, p.EmbeddingApiKeyEncrypted })
            .FirstOrDefaultAsync(ct);

        return new EmbeddingSettingsDto(
            Source: config.Source.ToString(),
            EffectiveApiBase: config.Enabled ? config.ApiBase : null,
            Model: config.Model,
            Dimension: config.Dimension,
            SavedApiBase: string.IsNullOrWhiteSpace(saved?.EmbeddingApiBase) ? null : saved.EmbeddingApiBase,
            SavedHasApiKey: !string.IsNullOrEmpty(saved?.EmbeddingApiKeyEncrypted),
            ServerApiBase: string.IsNullOrWhiteSpace(_emb.ApiBase) ? null : _emb.ApiBase.Trim());
    }

    private Guid? CallerId =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } uid && Guid.TryParse(uid, out var parsed)
            ? parsed
            : null;
}
