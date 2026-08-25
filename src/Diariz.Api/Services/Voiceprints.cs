using Pgvector;

namespace Diariz.Api.Services;

/// <summary>Pure helpers for combining speaker embeddings into a voiceprint centroid.</summary>
public static class Voiceprints
{
    /// <summary>Cosine distance between two embeddings, matching pgvector's <c>&lt;=&gt;</c> so a number
    /// shown in the UI means the same thing as one the database produced.</summary>
    public static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }
        // A zero vector has no direction, so it is not similar to anything.
        if (na <= 0 || nb <= 0) return 1;
        return 1 - (dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }

    /// <summary>The L2-normalised mean of the given embeddings, or null when there are none.</summary>
    public static Vector? Centroid(IReadOnlyList<float[]> embeddings)
    {
        if (embeddings.Count == 0) return null;
        var dim = embeddings[0].Length;
        var sum = new float[dim];
        foreach (var e in embeddings)
            for (var i = 0; i < dim && i < e.Length; i++)
                sum[i] += e[i];

        var norm = 0.0;
        for (var i = 0; i < dim; i++)
        {
            sum[i] /= embeddings.Count;
            norm += sum[i] * (double)sum[i];
        }
        norm = Math.Sqrt(norm);
        if (norm > 0)
            for (var i = 0; i < dim; i++)
                sum[i] = (float)(sum[i] / norm);

        return new Vector(sum);
    }
}
