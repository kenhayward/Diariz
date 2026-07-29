using Diariz.Api.Configuration;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Diariz.Api.Services;

/// <summary>A matched voiceprint and how close it was (cosine distance, lower = closer).</summary>
public record SpeakerMatch(Guid PersonId, string Name, double Distance);

public interface ISpeakerIdentifier
{
    /// <summary>The nearest enrolled voiceprint to <paramref name="embedding"/> within the configured
    /// threshold, or null when identification is disabled or nobody is close enough.
    ///
    /// <para>Deliberately takes no user: the directory is platform-wide, so there is no per-caller candidate
    /// set to narrow to. A <c>userId</c> parameter here would sit unused and invite someone to "fix" it back
    /// into a filter, quietly re-fragmenting the directory.</para></summary>
    Task<SpeakerMatch?> IdentifyAsync(Vector embedding, CancellationToken ct = default);
}

/// <summary>Matches a speaker embedding against the platform's voiceprints by pgvector cosine distance.</summary>
public class SpeakerIdentifier : ISpeakerIdentifier
{
    private readonly DiarizDbContext _db;
    private readonly IdentificationOptions _opts;

    public SpeakerIdentifier(DiarizDbContext db, IOptions<IdentificationOptions> opts)
    {
        _db = db;
        _opts = opts.Value;
    }

    public async Task<SpeakerMatch?> IdentifyAsync(Vector embedding, CancellationToken ct = default)
    {
        if (!_opts.Enabled) return null;

        // A person's voiceprint is optional, so much of the directory has no embedding to compare against:
        // someone added by hand, or one who opted out and had theirs erased. Both must be excluded before
        // the distance projection - CosineDistance over a NULL column does not do anything useful.
        //
        // This is a sequential scan over every enrolled person, and that is fine: measured at roughly
        // 0.35 ms per 1,000 people (0.12 ms at 250, 1.9 ms at 5,000), and it runs once per speaker per
        // transcription. An HNSW index takes 100k rows from 35 ms to 0.4 ms, but it is approximate - it can
        // miss the true nearest neighbour, and a miss here means a speaker silently goes unidentified. Not
        // worth that trade until the directory is very large; revisit past ~25,000 people (~10 ms).
        var best = await _db.People
            .Where(p => p.Embedding != null && !p.VoiceprintOptOut)
            .Select(p => new { p.Id, p.Name, Distance = p.Embedding!.CosineDistance(embedding) })
            .OrderBy(x => x.Distance)
            .FirstOrDefaultAsync(ct);

        if (best is null || best.Distance > _opts.Threshold) return null;
        return new SpeakerMatch(best.Id, best.Name, best.Distance);
    }
}
