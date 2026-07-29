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
        // Note this scan is now unbounded: it was per-user, and is every enrolled person on the platform.
        // There is no HNSW/IVFFlat index on the column yet; add one if the plan turns into a seq scan over
        // more than a few hundred rows.
        var best = await _db.People
            .Where(p => p.Embedding != null && !p.VoiceprintOptOut)
            .Select(p => new { p.Id, p.Name, Distance = p.Embedding!.CosineDistance(embedding) })
            .OrderBy(x => x.Distance)
            .FirstOrDefaultAsync(ct);

        if (best is null || best.Distance > _opts.Threshold) return null;
        return new SpeakerMatch(best.Id, best.Name, best.Distance);
    }
}
