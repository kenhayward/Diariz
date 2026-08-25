using Diariz.Api.Configuration;
using Diariz.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Diariz.Api.Services;

public interface ISpeakerIdentifier
{
    /// <summary>The nearest enrolled voiceprints to <paramref name="embedding"/>, ascending by cosine
    /// distance, at most <paramref name="take"/> of them.
    ///
    /// <para><b>Applies no threshold.</b> This returns evidence; deciding what to do with a distance belongs
    /// to <see cref="IdentificationRules"/>. Keeping a threshold here as well would give the system two
    /// places that each hold their own idea of what counts as a match, and the operating point has to be one
    /// number an administrator can calibrate.</para>
    ///
    /// <para>At most one entry per person, which is what makes the runner-up in the margin check a different
    /// human rather than the same one's second voiceprint.</para>
    ///
    /// <para>Deliberately takes no user: the directory is platform-wide, so there is no per-caller candidate
    /// set to narrow to. A <c>userId</c> parameter here would sit unused and invite someone to "fix" it back
    /// into a filter, quietly re-fragmenting the directory.</para></summary>
    Task<IReadOnlyList<RankedCandidate>> RankAsync(
        Vector embedding, int take = 2, CancellationToken ct = default);
}

/// <summary>Ranks a speaker embedding against the platform's voiceprints by pgvector cosine distance.</summary>
public class SpeakerIdentifier : ISpeakerIdentifier
{
    private readonly DiarizDbContext _db;
    private readonly IdentificationOptions _opts;

    public SpeakerIdentifier(DiarizDbContext db, IOptions<IdentificationOptions> opts)
    {
        _db = db;
        _opts = opts.Value;
    }

    public async Task<IReadOnlyList<RankedCandidate>> RankAsync(
        Vector embedding, int take = 2, CancellationToken ct = default)
    {
        if (!_opts.Enabled) return [];

        // A person's voiceprint is optional, so much of the directory has no embedding to compare against:
        // someone added by hand, or one who opted out and had theirs erased. Both must be excluded before the
        // distance projection - CosineDistance over a NULL column does not do anything useful.
        //
        // This is a sequential scan over every enrolled person, and that is fine: measured at roughly
        // 0.35 ms per 1,000 people (0.12 ms at 250, 1.9 ms at 5,000), and it runs once per speaker per
        // transcription. An HNSW index takes 100k rows from 35 ms to 0.4 ms, but it is approximate - it can
        // miss the true nearest neighbour, and a miss here means a speaker silently goes unidentified. Not
        // worth that trade until the directory is very large; revisit past ~25,000 people (~10 ms).
        // Projected to an anonymous type rather than straight to RankedCandidate: EF cannot order by a
        // member of a record it is constructing, so the mapping happens after the query.
        var rows = await _db.People
            .Where(p => p.Embedding != null && !p.VoiceprintOptOut)
            .Select(p => new { p.Id, p.Name, Distance = p.Embedding!.CosineDistance(embedding) })
            .OrderBy(x => x.Distance)
            .Take(Math.Max(1, take))
            .ToListAsync(ct);

        return rows.Select(r => new RankedCandidate(r.Id, r.Name, r.Distance)).ToList();
    }
}
