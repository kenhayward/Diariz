using Diariz.Api.Configuration;
using Diariz.Domain;
using Diariz.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

public interface IChatContextResolver
{
    /// <summary>Effective context-window size (tokens) for a chat turn: the context length of the model
    /// that actually serves chat, else the server default.</summary>
    Task<int> ResolveContextWindowAsync(CancellationToken ct = default);
}

/// <summary>Supplies the number the chat context dial reports against.
///
/// It used to be the user's own <c>ChatContextWindow</c> override. From 0.221.0 the window is a fact about
/// the model the platform assigns to chat, so it is read from that model - and the gauge and the actual
/// truncation stay in agreement, because <see cref="Llm.LlmSettingsResolver"/> sizes the budget from the
/// same column.</summary>
public class ChatContextResolver : IChatContextResolver
{
    private readonly DiarizDbContext _db;
    private readonly ChatOptions _opts;

    public ChatContextResolver(DiarizDbContext db, IOptions<ChatOptions> opts)
    {
        _db = db;
        _opts = opts.Value;
    }

    public async Task<int> ResolveContextWindowAsync(CancellationToken ct = default)
    {
        // Chat's own assignment first, then the platform default - the same order the settings resolver
        // walks. Deliberately not shared with it: that returns a character budget, and the dial needs the
        // raw token window.
        var assigned = await _db.LlmCallAssignments
            .Where(a => a.Group == LlmCallGroup.Chat)
            .Select(a => (Guid?)a.LlmModelId)
            .FirstOrDefaultAsync(ct);

        assigned ??= await _db.PlatformSettings
            .Where(p => p.Id == PlatformSettings.SingletonId)
            .Select(p => p.DefaultLlmModelId)
            .FirstOrDefaultAsync(ct);

        if (assigned is { } id)
        {
            var length = await _db.LlmModels
                .Where(m => m.Id == id)
                .Select(m => m.ContextLength)
                .FirstOrDefaultAsync(ct);

            if (length > 0) return length;
        }

        return _opts.ContextLength;
    }
}
