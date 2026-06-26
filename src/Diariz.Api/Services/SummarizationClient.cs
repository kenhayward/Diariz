using System.Net.Http.Headers;
using System.Net.Http.Json;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

public interface ISummarizationClient
{
    /// <summary>Summarise the segments against the resolved (per-user) config; when
    /// <paramref name="needName"/> is true, also asks the model for a short recording name.</summary>
    Task<SummaryResult> SummarizeAsync(
        SummarizationRequestConfig config, IReadOnlyList<SegmentDto> segments, bool needName,
        CancellationToken ct = default);
}

/// <summary>Calls an OpenAI-compatible /chat/completions endpoint using a per-request config.</summary>
public class SummarizationClient : ISummarizationClient
{
    private readonly HttpClient _http;

    public SummarizationClient(HttpClient http) => _http = http;

    public async Task<SummaryResult> SummarizeAsync(
        SummarizationRequestConfig config, IReadOnlyList<SegmentDto> segments, bool needName,
        CancellationToken ct = default)
    {
        var messages = SummarizationPrompt.BuildMessages(segments, needName);
        var body = new
        {
            model = config.Model,
            temperature = 0.3,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };

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
        return SummarizationPrompt.ParseResponse(json, needName);
    }
}
