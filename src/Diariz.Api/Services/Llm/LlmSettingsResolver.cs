using Diariz.Api.Configuration;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services.Llm;

/// <summary>Everything one LLM call needs: where to send it, as whom, and with which parameters.
///
/// Platform-wide from 0.221.0 - it used to be resolved per user, and the rename from
/// <c>SummarizationRequestConfig</c> reflects that it was never summarisation-specific.</summary>
public sealed record LlmRequestConfig(string ApiBase, string ApiKey, string Model, LlmParameters Parameters)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiBase);

    /// <summary>The request deadline. The HTTP clients have no cap of their own, so this is the only
    /// authority.</summary>
    public int TimeoutSeconds => Parameters.TimeoutSeconds;

    /// <summary>The <c>reasoning_effort</c> to send, or null to omit the field entirely - so a
    /// non-reasoning endpoint never sees it even when an effort is configured.</summary>
    public string? ReasoningEffort => Parameters.ReasoningEnabled ? Parameters.ReasoningEffort : null;

    /// <summary>Characters of context this request may inject - the <b>single</b> budget shared by every LLM
    /// call site, derived from the context window of the model actually serving the call. The default keeps
    /// hand-constructed configs (tests, fakes) at the floor rather than at zero.</summary>
    public int ContextCharBudget { get; init; } = LlmContextBudget.MinimumChars;

    /// <summary>Whether streamed requests ask the server for token counts via
    /// <c>stream_options.include_usage</c>. A toggle rather than a constant because an OpenAI-compatible
    /// endpoint that rejects the unknown field must be recoverable without a redeploy.</summary>
    public bool IncludeStreamUsage { get; init; } = true;
}

public interface ILlmSettingsResolver
{
    /// <summary>Resolves the model and parameters for a call of this kind.
    ///
    /// The kind is a parameter rather than being read from the ambient <see cref="LlmCallScope"/>, even
    /// though the scope already carries it. Ambient is fine for telemetry, where a missing scope logs
    /// <c>Unknown</c> and is visible and fixable; it is not fine for behaviour, where a missing scope would
    /// silently apply the wrong model. Every caller already knows its kind - it pushes the scope with it.
    ///
    /// <paramref name="modelOverride"/> is a model the END USER chose in the chat picker. It is honoured
    /// only for <see cref="LlmCallKind.ChatMessage"/>, and only for a model an administrator offers -
    /// <see cref="IChatModelCatalog"/> decides, and that check is the security boundary. It lives here
    /// rather than in the controller so that no future caller can skip it.</summary>
    Task<LlmRequestConfig> ResolveAsync(
        LlmCallKind kind, Guid? modelOverride, CancellationToken ct = default);

    /// <summary>No user-chosen model: every call site except a chat turn.</summary>
    Task<LlmRequestConfig> ResolveAsync(LlmCallKind kind, CancellationToken ct = default) =>
        ResolveAsync(kind, null, ct);
}

public class LlmSettingsResolver : ILlmSettingsResolver
{
    private readonly DiarizDbContext _db;
    private readonly LlmDefaultsOptions _defaults;
    private readonly SummarizationOptions _summary;
    private readonly IApiKeyProtector _protector;
    private readonly IChatModelCatalog _chatModels;
    private readonly ChatOptions _chat;

    /// <param name="summary">The environment endpoint, used only to synthesize a fallback model when the
    /// platform has none configured - which is what lets an upgrade keep working before an admin visits
    /// /admin/llm-models.</param>
    public LlmSettingsResolver(
        DiarizDbContext db, IOptions<LlmDefaultsOptions> defaults, IOptions<SummarizationOptions> summary,
        IApiKeyProtector protector, IChatModelCatalog chatModels, IOptions<ChatOptions>? chat = null)
    {
        _db = db;
        _defaults = defaults.Value;
        _summary = summary.Value;
        _protector = protector;
        _chatModels = chatModels;
        _chat = chat?.Value ?? new ChatOptions();
    }

    /// <summary>The no-override form. Declared on the class as well as defaulted on the interface, because
    /// a default interface method is reachable only through the interface - and the test harnesses, plus
    /// TestSupport's controller builders, construct this type directly.</summary>
    public Task<LlmRequestConfig> ResolveAsync(LlmCallKind kind, CancellationToken ct = default) =>
        ResolveAsync(kind, null, ct);

    public async Task<LlmRequestConfig> ResolveAsync(
        LlmCallKind kind, Guid? modelOverride, CancellationToken ct = default)
    {
        var group = LlmCallGroups.GroupFor(kind);
        var ps = await _db.PlatformSettings
            .FirstOrDefaultAsync(p => p.Id == PlatformSettings.SingletonId, ct);

        // A user-chosen model applies to a chat MESSAGE only. ChatTitle shares the Chat group but is
        // background housekeeping the user never sees, so it stays on whatever the administrator routed.
        var chosen = kind == LlmCallKind.ChatMessage
            ? await _chatModels.ResolveOfferedAsync(modelOverride, ct)
            : null;

        var model = await ChooseModelAsync(group, chosen, ps, ct);

        // Most specific first. A null layer is skipped, so a model with no override row for this group
        // inherits rather than omitting everything.
        var layers = new List<string?>
        {
            group is null || model is null ? null : ParametersFor(model, group.Value),
            model is null ? null : ParametersFor(model, LlmCallGroup.ModelBase),
        };
        // Shared with the model editor's test call, so the panel cannot disagree with the pipeline about
        // what a parameter resolves to. Carries the admin's platform timeout.
        layers.AddRange(LlmPlatformLayers.Below(_defaults, group, ps));

        var parameters = LlmParameterLayers.Resolve(layers);

        return new LlmRequestConfig(
            ApiBase: model?.ApiBase ?? _summary.ApiBase,
            ApiKey: model is null ? _summary.ApiKey : _protector.Unprotect(model.ApiKeyEncrypted) ?? "",
            Model: model?.Name ?? _summary.Model,
            Parameters: parameters)
        {
            ContextCharBudget = LlmContextBudget.CharsFor(
                model is not null ? model.ContextLength : _chat.ContextLength),
            IncludeStreamUsage = ps?.LlmStreamUsageEnabled ?? true,
        };
    }

    /// <summary>The group's assigned model, else the platform default, else null - which means fall back to
    /// the environment endpoint. The fallback is synthesized per call and never persisted: writing it would
    /// resurrect a row an admin had deliberately deleted.</summary>
    /// <param name="chosen">A model the user picked, already validated as offered by
    /// <see cref="IChatModelCatalog"/>. It outranks the routing table - that is the point of picking one -
    /// so nothing here re-checks it.</param>
    private async Task<LlmModel?> ChooseModelAsync(
        LlmCallGroup? group, Guid? chosen, PlatformSettings? ps, CancellationToken ct)
    {
        Guid? id = chosen;

        if (id is null && group is not null)
            id = await _db.LlmCallAssignments
                .Where(a => a.Group == group.Value)
                .Select(a => (Guid?)a.LlmModelId)
                .FirstOrDefaultAsync(ct);

        id ??= ps?.DefaultLlmModelId;
        if (id is null) return null;

        return await _db.LlmModels
            .Include(m => m.Parameters)
            .FirstOrDefaultAsync(m => m.Id == id.Value, ct);
    }

    private static string? ParametersFor(LlmModel model, LlmCallGroup group) =>
        model.Parameters.FirstOrDefault(p => p.Group == group)?.ParametersJson;
}
