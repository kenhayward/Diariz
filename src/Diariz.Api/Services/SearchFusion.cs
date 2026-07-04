namespace Diariz.Api.Services;

/// <summary>
/// Reciprocal Rank Fusion (RRF) of two ranked <see cref="TranscriptHit"/> lists - the lexical (trigram)
/// arm and the semantic (vector) arm of hybrid search. Each hit contributes <c>1 / (k + rank)</c> to its
/// key's score; a hit surfaced by both arms accumulates both contributions and rises to the top. Pure and
/// order-preserving so the fusion is unit-testable without a database. Hits are keyed by
/// <c>(RecordingId, StartMs)</c>; when both arms produce the same key, the first-seen (lexical, segment-precise)
/// hit is kept for citation, but it takes the combined score.
/// </summary>
public static class SearchFusion
{
    /// <summary>RRF damping constant. 60 is the standard value from the original RRF paper - large enough that
    /// the score gap between adjacent ranks is gentle, so an item ranked highly by both arms beats one ranked
    /// top by a single arm.</summary>
    public const int DefaultK = 60;

    public static IReadOnlyList<TranscriptHit> Fuse(
        IReadOnlyList<TranscriptHit> lexical, IReadOnlyList<TranscriptHit> semantic, int limit, int k = DefaultK)
    {
        // Preserve first-seen insertion order for stable ties, and keep the first hit encountered per key.
        var order = new List<(Guid RecordingId, long StartMs)>();
        var scores = new Dictionary<(Guid, long), double>();
        var hits = new Dictionary<(Guid, long), TranscriptHit>();

        void Accumulate(IReadOnlyList<TranscriptHit> list)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var h = list[rank];
                var key = (h.RecordingId, h.StartMs);
                var contribution = 1.0 / (k + rank + 1);
                if (scores.TryGetValue(key, out var existing))
                {
                    scores[key] = existing + contribution;
                }
                else
                {
                    scores[key] = contribution;
                    hits[key] = h;
                    order.Add(key);
                }
            }
        }

        Accumulate(lexical);
        Accumulate(semantic);

        return order
            .OrderByDescending(key => scores[key]) // OrderBy is stable → insertion order breaks score ties
            .Take(Math.Max(0, limit))
            .Select(key => hits[key] with { Similarity = scores[key] })
            .ToList();
    }
}
