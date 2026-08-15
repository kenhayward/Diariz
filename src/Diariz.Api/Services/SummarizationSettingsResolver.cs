using Diariz.Api.Configuration;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>Characters of context this request may inject - the <b>single</b> budget shared by every LLM
    /// call site, derived by the resolver from the user's effective model context window. It rides on this
    /// record because this record already reaches every client, prompt builder and worker. The default keeps
    /// hand-constructed configs (tests, fakes) at the floor rather than at zero.</summary>
    public int ContextCharBudget { get; init; } = LlmContextBudget.MinimumChars;
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
    private readonly ChatOptions _chat;

    /// <param name="chat">Supplies the server-wide model context window. Optional so the many hand-built
    /// resolvers in the test suite keep compiling; DI always supplies it.</param>
    public SummarizationSettingsResolver(
        DiarizDbContext db, IOptions<SummarizationOptions> opts, IApiKeyProtector protector,
        IOptions<ChatOptions>? chat = null)
    {
        _db = db;
        _opts = opts.Value;
        _protector = protector;
        _chat = chat?.Value ?? new ChatOptions();
    }

    public async Task<SummarizationRequestConfig> ResolveAsync(Guid userId, CancellationToken ct = default)
    {
        var s = await _db.UserSettings.FindAsync([userId], ct);

        // Reasoning: user override ?? server default. When on, the effort is the user's value ?? the
        // server's; when off, leave it null so clients omit the param entirely.
        var reasoningOn = s?.ReasoningEnabled ?? _opts.ReasoningEnabled;
        var effort = reasoningOn ? Coalesce(s?.ReasoningEffort, _opts.ReasoningEffort) : null;

        // The request timeout is the user's override ?? the platform-wide admin setting ?? the server
        // option. The HTTP clients themselves have no cap, so this is the single authority.
        var ps = await _db.PlatformSettings
            .FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId, ct);

        return new SummarizationRequestConfig(
            ApiBase: Coalesce(s?.SummaryApiBase, _opts.ApiBase),
            ApiKey: Coalesce(_protector.Unprotect(s?.SummaryApiKeyEncrypted), _opts.ApiKey),
            Model: Coalesce(s?.SummaryModel, _opts.Model),
            TimeoutSeconds: s?.LlmTimeoutSeconds ?? ps?.LlmTimeoutSeconds ?? _opts.TimeoutSeconds)
        {
            ReasoningEffort = effort,
            // One budget for every LLM call, sized off the model window the user actually points at. The
            // per-user override is the same field the chat context dial reads, so the gauge and the real
            // truncation finally agree.
            ContextCharBudget = LlmContextBudget.CharsFor(
                s?.ChatContextWindow is > 0 ? s.ChatContextWindow.Value : _chat.ContextLength),
        };
    }

    private static string Coalesce(string? user, string server) =>
        string.IsNullOrWhiteSpace(user) ? server : user.Trim();
}
