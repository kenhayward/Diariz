using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Diariz.Api.Services;

public interface IEmbeddingClient
{
    /// <summary>Embeds each input string against the resolved config, batched by <c>BatchSize</c>. Returns one
    /// vector per input, in input order.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        EmbeddingRequestConfig config, IReadOnlyList<string> inputs, CancellationToken ct = default);
}

/// <summary>Calls an OpenAI-compatible <c>/embeddings</c> endpoint using a per-request config.</summary>
public class EmbeddingClient : IEmbeddingClient
{
    private readonly HttpClient _http;

    public EmbeddingClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        EmbeddingRequestConfig config, IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        var results = new List<float[]>(inputs.Count);
        var batchSize = Math.Max(1, config.BatchSize);

        for (var offset = 0; offset < inputs.Count; offset += batchSize)
        {
            var batch = inputs.Skip(offset).Take(batchSize).ToArray();
            results.AddRange(await EmbedBatchAsync(config, batch, ct));
        }

        return results;
    }

    private async Task<float[][]> EmbedBatchAsync(
        EmbeddingRequestConfig config, string[] batch, CancellationToken ct)
    {
        var body = new { model = config.Model, input = batch };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{config.ApiBase.TrimEnd('/')}/embeddings")
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrEmpty(config.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        using var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        return ParseVectors(json, batch.Length);
    }

    /// <summary>Parses the OpenAI embeddings response into <paramref name="count"/> vectors, ordered by the
    /// response's <c>index</c> field (providers may return the data array out of order).</summary>
    private static float[][] ParseVectors(string responseJson, int count)
    {
        var vectors = new float[count][];
        using var doc = JsonDocument.Parse(responseJson);
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var index = item.TryGetProperty("index", out var idx) ? idx.GetInt32() : Array.IndexOf(vectors, null);
            var vec = item.GetProperty("embedding").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            if (index >= 0 && index < count) vectors[index] = vec;
        }
        return vectors;
    }
}
