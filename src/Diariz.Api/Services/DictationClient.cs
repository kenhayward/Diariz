using System.Net.Http.Headers;
using System.Text.Json;

namespace Diariz.Api.Services;

/// <summary>Effective dictation config for one request (server-level; no per-user override).</summary>
public record DictationRequestConfig(string ApiBase, string ApiKey, string Model, int TimeoutSeconds);

public interface IDictationClient
{
    /// <summary>Transcribe one short audio utterance via an OpenAI-compatible /audio/transcriptions
    /// endpoint. Returns the recognised text (may be empty for silence).</summary>
    Task<string> TranscribeAsync(
        DictationRequestConfig config, Stream audio, string contentType, string fileName,
        CancellationToken ct = default);
}

/// <summary>Calls an OpenAI-compatible /audio/transcriptions endpoint with a multipart body.</summary>
public class DictationClient : IDictationClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public DictationClient(HttpClient http) => _http = http;

    public async Task<string> TranscribeAsync(
        DictationRequestConfig config, Stream audio, string contentType, string fileName,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(audio);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        form.Add(file, "file", fileName);
        form.Add(new StringContent(config.Model), "model");

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{config.ApiBase.TrimEnd('/')}/audio/transcriptions") { Content = form };
        if (!string.IsNullOrEmpty(config.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        using var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
    }
}
