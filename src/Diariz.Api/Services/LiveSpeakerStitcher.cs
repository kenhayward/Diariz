namespace Diariz.Api.Services;

/// <summary>A voice the meeting has already heard, and the running mean of the vectors seen for it.</summary>
/// <param name="Samples">How many chunk vectors have been folded in. Used by <see
/// cref="LiveSpeakerStitcher.UpdateCentroid"/> so a centroid built from ten chunks is not swung around
/// by an eleventh.</param>
public record SessionCentroid(string Label, float[] Centroid, int Samples);

/// <summary>One speaker pyannote found inside a single chunk. Its label is meaningful only within that
/// chunk - the same person is very likely a different number in the next one.</summary>
public record ChunkSpeaker(string Label, float[] Embedding, long SpeechMs);

/// <summary>The operating point for stitching. Separate from <see cref="IdentificationThresholds"/> on
/// purpose: that decides whether a voice is a <b>named person</b>, this decides whether two chunks hold
/// the <b>same voice</b>. They are different questions against different evidence - one compares against
/// a voiceprint enrolled from minutes of clean audio, the other against a centroid built from seconds -
/// and collapsing them onto one number would mean tuning either would silently retune the other.</summary>
public record StitchThresholds(double Threshold, double Margin);

/// <summary>What to do with one chunk-local speaker.</summary>
/// <param name="IsNew">True when this voice is being heard for the first time, so
/// <paramref name="SessionLabel"/> was minted rather than matched.</param>
public record StitchDecision(string ChunkLabel, string SessionLabel, bool IsNew, double Distance);

/// <summary>
/// Gives a voice one label for the whole meeting.
///
/// <para>pyannote clusters over whatever audio it is handed, so a live chunk's speaker labels are
/// internally consistent and externally meaningless: SPEAKER_00 in this half minute has no relationship
/// to SPEAKER_00 in the next. Without stitching, a transcript would renumber its speakers every thirty
/// seconds - which is why phase 2 showed no labels at all rather than showing those.</para>
///
/// <para>Pure, and deliberately the only hard thing in this phase. It is testable with hand-built
/// vectors, no database, no GPU and no audio, which is the whole reason the decision lives here rather
/// than inside the callback that applies it.</para>
/// </summary>
public static class LiveSpeakerStitcher
{
    /// <summary>Match this chunk's speakers onto the meeting's running voices, minting where nothing fits.
    ///
    /// <para>Two failure modes are guarded, and they pull in opposite directions. pyannote
    /// <b>over-segments</b> short windows - measured in the S0 benchmark, it found three speakers in a
    /// 20 s clip containing two - so several chunk labels legitimately collapse onto one session label
    /// across chunks, and assuming a bijection would mint a new voice every chunk. But within a
    /// <b>single</b> chunk it has already decided these are different people, so two of them must never
    /// take the same session label: that would file two people's words under one name, which reads far
    /// worse than an extra speaker.</para></summary>
    public static IReadOnlyList<StitchDecision> Stitch(
        IReadOnlyList<SessionCentroid> known, IReadOnlyList<ChunkSpeaker> incoming, StitchThresholds t)
    {
        var decisions = new StitchDecision[incoming.Count];
        var taken = new HashSet<string>(StringComparer.Ordinal);

        // Every (chunk speaker, known voice) pair, nearest first. Working globally rather than per
        // speaker in order is what stops the result depending on pyannote's arbitrary numbering: when
        // two chunk speakers both want one centroid, the closer one gets it either way round.
        var pairs = new List<(int Index, string Label, double Distance)>();
        for (var i = 0; i < incoming.Count; i++)
            foreach (var k in known)
                pairs.Add((i, k.Label, CosineDistance(incoming[i].Embedding, k.Centroid)));
        pairs.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        foreach (var (index, label, distance) in pairs)
        {
            if (decisions[index] is not null || taken.Contains(label)) continue;
            if (distance >= t.Threshold) continue;

            // Clear air over the runner-up, measured against the next *other* voice - so a centroid this
            // speaker has already been given cannot veto itself. Without it, a voice sitting between two
            // known speakers is assigned by a coin flip that every later chunk then inherits.
            var runnerUp = pairs
                .Where(p => p.Index == index && p.Label != label && !taken.Contains(p.Label))
                .Select(p => (double?)p.Distance)
                .FirstOrDefault();
            if (runnerUp is { } second && second - distance < t.Margin) continue;

            decisions[index] = new StitchDecision(incoming[index].Label, label, IsNew: false, distance);
            taken.Add(label);
        }

        // Whatever is left is a voice this meeting has not heard before. Minted labels must not collide
        // with one already in use, or two voices would silently merge for the rest of the meeting.
        var used = new HashSet<string>(known.Select(k => k.Label), StringComparer.Ordinal);
        foreach (var d in decisions) if (d is not null) used.Add(d.SessionLabel);

        var next = 0;
        for (var i = 0; i < decisions.Length; i++)
        {
            if (decisions[i] is not null) continue;
            string label;
            do { label = $"SPEAKER_{next++:D2}"; } while (!used.Add(label));
            decisions[i] = new StitchDecision(incoming[i].Label, label, IsNew: true, double.NaN);
        }

        return decisions;
    }

    /// <summary>Fold one more chunk vector into a session's running centroid.
    ///
    /// <para>A running mean, weighted by how much has already been heard, then re-normalised. The
    /// weighting is what stops a voice being redefined by its worst chunk: measured and recorded in spec
    /// §6.4, ECAPA on 15-30 s of a single voice is noisy, and that noise is the real floor under chunk
    /// length. A centroid whose tenth sample moved it as far as its second would inherit every bad chunk
    /// in full. It must still move, though - a centroid that froze could never be corrected by later
    /// evidence, and correcting an early wrong guess is exactly what the extra evidence is for.</para>
    ///
    /// <para>Re-normalisation matters beyond this class: cosine distance would not notice a drifting
    /// magnitude, but this vector is stored on <c>Speaker.Embedding</c> and later ranked in Postgres
    /// against voiceprints the worker L2-normalises. An un-normalised centroid would mean something
    /// subtly different from every other embedding in the database.</para></summary>
    public static SessionCentroid UpdateCentroid(SessionCentroid current, float[] observed)
    {
        var n = Math.Max(current.Samples, 1);
        var mean = new float[current.Centroid.Length];
        for (var i = 0; i < mean.Length && i < observed.Length; i++)
            mean[i] = (float)((current.Centroid[i] * (double)n + observed[i]) / (n + 1));

        return current with { Centroid = Normalise(mean), Samples = current.Samples + 1 };
    }

    private static float[] Normalise(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * (double)x;
        var norm = Math.Sqrt(sum);
        if (norm <= 0) return v;
        var outv = new float[v.Length];
        for (var i = 0; i < v.Length; i++) outv[i] = (float)(v[i] / norm);
        return outv;
    }

    /// <summary>Cosine distance, matching pgvector's <c>&lt;=&gt;</c> so a distance means the same thing
    /// here as it does when the same vector is ranked against the voiceprint directory in Postgres.</summary>
    public static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }
        if (na <= 0 || nb <= 0) return 1.0;
        return 1.0 - dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
