using Pgvector;

namespace Diariz.Api.Services;

/// <summary>Pure helpers for combining speaker embeddings into a voiceprint centroid.</summary>
public static class Voiceprints
{
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
