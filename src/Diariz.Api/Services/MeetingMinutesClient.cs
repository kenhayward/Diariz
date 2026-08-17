using Diariz.Api.Services.Llm;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Diariz.Api.Services;

public interface IMeetingMinutesClient
{
    /// <summary>Generate meeting minutes (Markdown) from the pre-built chat messages against the resolved
    /// (per-user) config. Returns the model's Markdown.</summary>
    Task<string> GenerateAsync(
        LlmRequestConfig config, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
}

/// <summary>Calls an OpenAI-compatible /chat/completions endpoint to produce meeting minutes, reusing the
/// same per-request summarisation config (endpoint/model/key/reasoning).</summary>
public class MeetingMinutesClient : IMeetingMinutesClient
{
    private readonly HttpClient _http;

    public MeetingMinutesClient(HttpClient http) => _http = http;

    public async Task<string> GenerateAsync(
        LlmRequestConfig config, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        LlmRequestBody.Apply(body, config.Parameters);

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
        return MeetingMinutesPrompt.CleanResponse(json);
    }
}
