using Diariz.Api.Configuration;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>A matched voiceprint and how close it was (cosine distance, lower = closer).</summary>
public record SpeakerMatch(Guid ProfileId, string Name, double Distance);

public interface ISpeakerIdentifier
{
    /// <summary>The user's nearest enrolled voiceprint to <paramref name="embedding"/> within the configured
    /// threshold, or null when identification is disabled / no profile is close enough.</summary>
    Task<SpeakerMatch?> IdentifyAsync(Guid userId, Vector embedding, CancellationToken ct = default);
}

/// <summary>Matches a speaker embedding against the user's enrolled voiceprints by pgvector cosine distance.</summary>
public class SpeakerIdentifier : ISpeakerIdentifier
{
    private readonly DiarizDbContext _db;
    private readonly IdentificationOptions _opts;

    public SpeakerIdentifier(DiarizDbContext db, IOptions<IdentificationOptions> opts)
    {
        _db = db;
        _opts = opts.Value;
    }

    public async Task<SpeakerMatch?> IdentifyAsync(Guid userId, Vector embedding, CancellationToken ct = default)
    {
        if (!_opts.Enabled) return null;

        var best = await _db.SpeakerProfiles
            .Where(p => p.UserId == userId)
            .Select(p => new { p.Id, p.Name, Distance = p.Embedding.CosineDistance(embedding) })
            .OrderBy(x => x.Distance)
            .FirstOrDefaultAsync(ct);

        if (best is null || best.Distance > _opts.Threshold) return null;
        return new SpeakerMatch(best.Id, best.Name, best.Distance);
    }
}
