using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diariz.Api.Services.Llm;

/// <summary>One model a chat user may pick.
///
/// Carries no endpoint and no key: this is the shape returned to every signed-in user, unlike
/// <c>LlmModelDto</c>, which is administrator-only for exactly that reason. <see cref="Name"/> is the slug
/// the server sends as <c>model</c>, present so a client can match a streamed usage snapshot back to a
/// label.</summary>
public sealed record ChatModelOption(Guid Id, string Label, string Name, int ContextLength, bool IsDefault);

public interface IChatModelCatalog
{
    /// <summary>The model that serves chat when the user has chosen nothing: the Chat group's assignment,
    /// else the platform default, else null - meaning the environment fallback, which has no row.</summary>
    Task<Guid?> DefaultModelIdAsync(CancellationToken ct = default);

    /// <summary>What the picker offers: the default first, then every chat-enabled model by label.</summary>
    Task<IReadOnlyList<ChatModelOption>> ListAsync(CancellationToken ct = default);

    /// <summary>The model id that should actually serve a turn, given what the caller asked for: the
    /// request's choice when it is offered, otherwise null - meaning "fall through to normal routing".</summary>
    Task<Guid?> ResolveOfferedAsync(Guid? requested, CancellationToken ct = default);
}

/// <summary>The single authority on which models chat may use.
///
/// It is its own service because three callers need the same answer: <see cref="LlmSettingsResolver"/>
/// (which endpoint to call), <see cref="Services.ChatContextResolver"/> (which window to report), and
/// <c>ChatModelsController</c> (what to offer). Written separately in each they would agree by coincidence
/// and diverge on the first change, producing a picker that offers a model the resolver then silently
/// refuses - or, worse, the reverse.
///
/// <b>The chat-assigned model is offered whether or not its flag is set.</b> It is the model actually in
/// use, so excluding it would leave the picker unable to show the current selection.</summary>
public sealed class ChatModelCatalog(DiarizDbContext db) : IChatModelCatalog
{
    public async Task<Guid?> DefaultModelIdAsync(CancellationToken ct = default)
    {
        var assigned = await db.LlmCallAssignments
            .Where(a => a.Group == LlmCallGroup.Chat)
            .Select(a => (Guid?)a.LlmModelId)
            .FirstOrDefaultAsync(ct);

        return assigned ?? await db.PlatformSettings
            .Where(p => p.Id == PlatformSettings.SingletonId)
            .Select(p => p.DefaultLlmModelId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ChatModelOption>> ListAsync(CancellationToken ct = default)
    {
        var defaultId = await DefaultModelIdAsync(ct);

        var models = await db.LlmModels
            .Where(m => m.ChatEnabled || (defaultId != null && m.Id == defaultId))
            .AsNoTracking()
            .ToListAsync(ct);

        // Ordered in memory rather than in SQL: Label is a C# computed property, so there is no column to
        // sort on. The set is a handful of rows at most.
        return models
            .Select(m => new ChatModelOption(m.Id, m.Label, m.Name, m.ContextLength, m.Id == defaultId))
            .OrderByDescending(o => o.IsDefault)
            .ThenBy(o => o.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<Guid?> ResolveOfferedAsync(Guid? requested, CancellationToken ct = default)
    {
        if (requested is not { } id) return null;

        // The default is offered implicitly, so it never needs the flag checked.
        if (id == await DefaultModelIdAsync(ct)) return id;

        return await db.LlmModels.AnyAsync(m => m.Id == id && m.ChatEnabled, ct) ? id : null;
    }
}
