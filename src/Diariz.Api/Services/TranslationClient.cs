using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Diariz.Api.Services;

public interface ITranslationClient
{
    /// <summary>Translate <paramref name="texts"/> into <paramref name="targetLanguage"/> (an English
    /// language name). Returns a list aligned 1:1 with the input — each entry is the translation, or the
    /// original text when the model dropped/garbled that item (so callers never lose content). Blank inputs
    /// pass through untouched.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        SummarizationRequestConfig config, string targetLanguage, IReadOnlyList<string> texts,
        CancellationToken ct = default);
}

/// <summary>Calls an OpenAI-compatible /chat/completions endpoint to translate text, batching by a
/// character budget so each request stays bounded. Mirrors <see cref="ActionsClient"/>.</summary>
public class TranslationClient : ITranslationClient
{
    /// <summary>Upper bound on source characters per request (keeps each call bounded; large transcripts
    /// span several calls).</summary>
    public const int BatchCharBudget = 12000;

    private readonly HttpClient _http;

    public TranslationClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<string>> TranslateAsync(
        SummarizationRequestConfig config, string targetLanguage, IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        // Default every slot to its source text; only non-blank items are sent and overwritten on success.
        var result = texts.ToArray();

        var batch = new List<(int Index, string Text)>();
        var batchChars = 0;
        for (var i = 0; i < texts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i])) continue;
            batch.Add((i, texts[i]));
            batchChars += texts[i].Length;
            if (batchChars >= BatchCharBudget)
            {
                await TranslateBatchAsync(config, targetLanguage, batch, result, ct);
                batch.Clear();
                batchChars = 0;
            }
        }
        if (batch.Count > 0) await TranslateBatchAsync(config, targetLanguage, batch, result, ct);

        return result;
    }

    private async Task TranslateBatchAsync(
        SummarizationRequestConfig config, string targetLanguage,
        IReadOnlyList<(int Index, string Text)> batch, string[] result, CancellationToken ct)
    {
        var messages = TranslationPrompt.BuildMessages(targetLanguage, batch);
        var body = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
            ["temperature"] = 0.1,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        if (config.ReasoningEffort is not null) body["reasoning_effort"] = config.ReasoningEffort;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{config.ApiBase.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(body)
        };
        if (!string.IsNullOrEmpty(config.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        using var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cts.Token);

        var map = TranslationPrompt.ParseTranslations(json);
        foreach (var (index, _) in batch)
            if (map.TryGetValue(index, out var t)) result[index] = t;
    }
}
