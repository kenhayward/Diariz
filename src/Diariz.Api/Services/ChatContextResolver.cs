using Diariz.Api.Configuration;
using Diariz.Domain;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

public interface IChatContextResolver
{
    /// <summary>Effective context-window size (tokens) for the user: their override, else the server default.</summary>
    Task<int> ResolveContextWindowAsync(Guid userId, CancellationToken ct = default);
}

public class ChatContextResolver : IChatContextResolver
{
    private readonly DiarizDbContext _db;
    private readonly ChatOptions _opts;

    public ChatContextResolver(DiarizDbContext db, IOptions<ChatOptions> opts)
    {
        _db = db;
        _opts = opts.Value;
    }

    public async Task<int> ResolveContextWindowAsync(Guid userId, CancellationToken ct = default)
    {
        var s = await _db.UserSettings.FindAsync([userId], ct);
        return s?.ChatContextWindow is > 0 ? s.ChatContextWindow.Value : _opts.ContextLength;
    }
}
