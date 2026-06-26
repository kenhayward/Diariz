using System.Net.Http.Headers;
using System.Net.Http.Json;
using Diariz.Api.Configuration;
using Diariz.Api.Contracts;
using Microsoft.Extensions.Options;

namespace Diariz.Api.Services;

public interface ISummarizationClient
{
    /// <summary>Summarise the segments; when <paramref name="needName"/> is true, also asks the
    /// model for a short recording name.</summary>
    Task<SummaryResult> SummarizeAsync(
        IReadOnlyList<SegmentDto> segments, bool needName, CancellationToken ct = default);
}

/// <summary>Calls an OpenAI-compatible /chat/completions endpoint.</summary>
public class SummarizationClient : ISummarizationClient
{
    private readonly HttpClient _http;
    private readonly SummarizationOptions _opts;

    public SummarizationClient(HttpClient http, IOptions<SummarizationOptions> opts)
    {
        _http = http;
        _opts = opts.Value;
    }

    public async Task<SummaryResult> SummarizeAsync(
        IReadOnlyList<SegmentDto> segments, bool needName, CancellationToken ct = default)
    {
        var messages = SummarizationPrompt.BuildMessages(segments, needName);
        var body = new
        {
            model = _opts.Model,
            temperature = 0.3,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.ApiBase.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(body)
        };
        if (!string.IsNullOrEmpty(_opts.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSeconds));

        using var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        return SummarizationPrompt.ParseResponse(json, needName);
    }
}
