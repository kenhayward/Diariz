using Diariz.Api.Services.Llm;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

public interface ITagsClient
{
    /// <summary>Ask the resolved (per-user) LLM to extract weighted tag-cloud tags from the segments using
    /// the given prompt <paramref name="template"/>. Returns an empty list when the transcript is too thin
    /// to tag.</summary>
    Task<IReadOnlyList<ExtractedTag>> ExtractAsync(
        LlmRequestConfig config, IReadOnlyList<SegmentDto> segments, string template,
        CancellationToken ct = default);
}

/// <summary>Calls an OpenAI-compatible /chat/completions endpoint to extract tags, using a per-request
/// config. Mirrors <see cref="ActionsClient"/>.</summary>
public class TagsClient : ITagsClient
{
    private readonly HttpClient _http;

    public TagsClient(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<ExtractedTag>> ExtractAsync(
        LlmRequestConfig config, IReadOnlyList<SegmentDto> segments, string template,
        CancellationToken ct = default)
    {
        var messages = TagsPrompt.BuildMessages(template, segments, config.ContextCharBudget);
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
        await LlmResponse.EnsureSuccessAsync(resp, cts.Token);
        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        return TagsPrompt.ParseResponse(json);
    }
}
