using Diariz.Api.Configuration;
using Diariz.Api.Services.Llm;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

public interface IChatContextResolver
{
    /// <summary>Effective context-window size (tokens) for a chat turn: the context length of the model
    /// that actually serves the turn, else the server default.
    ///
    /// <paramref name="modelOverride"/> is the model the user picked, honoured on exactly the same terms as
    /// in <see cref="ILlmSettingsResolver"/> - both defer to <see cref="IChatModelCatalog"/>, so the dial
    /// cannot report a window the request will not use.</summary>
    Task<int> ResolveContextWindowAsync(Guid? modelOverride, CancellationToken ct = default);

    /// <summary>No user-chosen model.</summary>
    Task<int> ResolveContextWindowAsync(CancellationToken ct = default) =>
        ResolveContextWindowAsync(null, ct);
}

/// <summary>Supplies the number the chat context dial reports against.
///
/// It used to be the user's own <c>ChatContextWindow</c> override. From 0.221.0 the window is a fact about
/// the model serving chat, so it is read from that model - and the gauge and the actual truncation stay in
/// agreement, because <see cref="Llm.LlmSettingsResolver"/> sizes the budget from the same column. From
/// 0.231.0 that model can be one the user picked, and both resolvers ask
/// <see cref="IChatModelCatalog"/> the same question so they cannot disagree about which it is.</summary>
public class ChatContextResolver : IChatContextResolver
{
    private readonly DiarizDbContext _db;
    private readonly IChatModelCatalog _chatModels;
    private readonly ChatOptions _opts;

    public ChatContextResolver(DiarizDbContext db, IOptions<ChatOptions> opts, IChatModelCatalog chatModels)
    {
        _db = db;
        _opts = opts.Value;
        _chatModels = chatModels;
    }

    /// <summary>The no-override form. Declared on the class as well as defaulted on the interface, because
    /// a default interface method is reachable only through the interface - and several harnesses construct
    /// this type directly.</summary>
    public Task<int> ResolveContextWindowAsync(CancellationToken ct = default) =>
        ResolveContextWindowAsync(null, ct);

    public async Task<int> ResolveContextWindowAsync(Guid? modelOverride, CancellationToken ct = default)
    {
        // The picked model when it is offered, else whatever the administrator routed chat to - the same
        // order the settings resolver walks, because it is the same catalog answering.
        var id = await _chatModels.ResolveOfferedAsync(modelOverride, ct)
                 ?? await _chatModels.DefaultModelIdAsync(ct);

        if (id is { } modelId)
        {
            var length = await _db.LlmModels
                .Where(m => m.Id == modelId)
                .Select(m => m.ContextLength)
                .FirstOrDefaultAsync(ct);

            if (length > 0) return length;
        }

        return _opts.ContextLength;
    }
}
