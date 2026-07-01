using System.Net.Http.Headers;
using System.Net.Http.Json;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

public interface IMeetingMinutesClient
{
    /// <summary>Generate meeting minutes (Markdown) for the segments against the resolved (per-user) config,
    /// optionally seeding the prompt with the meeting date. Returns the model's Markdown.</summary>
    Task<string> GenerateAsync(
        SummarizationRequestConfig config, IReadOnlyList<SegmentDto> segments, DateTimeOffset? meetingDate,
        int charBudget, CancellationToken ct = default);
}

/// <summary>Calls an OpenAI-compatible /chat/completions endpoint to produce meeting minutes, reusing the
/// same per-request summarisation config (endpoint/model/key/reasoning).</summary>
public class MeetingMinutesClient : IMeetingMinutesClient
{
    private readonly HttpClient _http;

    public MeetingMinutesClient(HttpClient http) => _http = http;

    public async Task<string> GenerateAsync(
        SummarizationRequestConfig config, IReadOnlyList<SegmentDto> segments, DateTimeOffset? meetingDate,
        int charBudget, CancellationToken ct = default)
    {
        var messages = MeetingMinutesPrompt.BuildMessages(segments, meetingDate, charBudget);
        var body = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
            ["temperature"] = 0.3,
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
        return MeetingMinutesPrompt.CleanResponse(json);
    }
}
