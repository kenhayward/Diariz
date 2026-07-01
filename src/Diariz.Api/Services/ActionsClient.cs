using System.Net.Http.Headers;
using System.Net.Http.Json;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

public interface IActionsClient
{
    /// <summary>Ask the resolved (per-user) LLM to extract action items from the segments using the given
    /// prompt <paramref name="template"/>. Returns an empty list when the transcript has none.</summary>
    Task<IReadOnlyList<ExtractedAction>> ExtractAsync(
        SummarizationRequestConfig config, IReadOnlyList<SegmentDto> segments, string template,
        CancellationToken ct = default);
}

/// <summary>Calls an OpenAI-compatible /chat/completions endpoint to extract actions, using a per-request
/// config. Mirrors <see cref="SummarizationClient"/>.</summary>
public class ActionsClient : IActionsClient
{
    private readonly HttpClient _http;

    public ActionsClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<ExtractedAction>> ExtractAsync(
        SummarizationRequestConfig config, IReadOnlyList<SegmentDto> segments, string template,
        CancellationToken ct = default)
    {
        var messages = ActionsPrompt.BuildMessages(template, segments);
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
        return ActionsPrompt.ParseResponse(json);
    }
}
