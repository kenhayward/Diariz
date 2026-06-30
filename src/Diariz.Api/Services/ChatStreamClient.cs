using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Diariz.Api.Services;

/// <summary>Raised when the streaming chat endpoint fails (non-success status or transport error).</summary>
public sealed class ChatStreamException : Exception
{
    public ChatStreamException(string message) : base(message) { }
}

/// <summary>One streamed chunk: a content delta and/or tool-call fragments and/or the finish reason.
/// All fields may be null in a given chunk.</summary>
public sealed record ChatStreamDelta(
    string? Content, IReadOnlyList<ToolCallFragment>? ToolCalls, string? FinishReason);

/// <summary>A streamed slice of a tool call. OpenAI sends these incrementally: the first fragment for an
/// <paramref name="Index"/> carries the id + name, later fragments append <paramref name="Arguments"/>.</summary>
public sealed record ToolCallFragment(int Index, string? Id, string? Name, string? Arguments);

/// <summary>A fully-assembled tool call.</summary>
public sealed record ToolCall(string Id, string Name, string Arguments);

public interface IChatStreamClient
{
    /// <summary>Streams the assistant reply token-by-token from an OpenAI-compatible
    /// <c>/chat/completions</c> endpoint (SSE, <c>stream:true</c>), using a per-request config.</summary>
    IAsyncEnumerable<string> StreamAsync(
        SummarizationRequestConfig config, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);

    /// <summary>Streams a turn that may use tools: yields content deltas, tool-call fragments and the finish
    /// reason. <paramref name="messages"/> are pre-shaped OpenAI message objects (so the caller can include
    /// assistant <c>tool_calls</c> and <c>tool</c> result messages); <paramref name="tools"/> is the tool spec
    /// array (null/empty = no tools offered).</summary>
    IAsyncEnumerable<ChatStreamDelta> StreamChunksAsync(
        SummarizationRequestConfig config, IReadOnlyList<object> messages,
        IReadOnlyList<object>? tools, CancellationToken ct = default);
}

/// <summary>Calls an OpenAI-compatible streaming /chat/completions endpoint and yields content deltas.</summary>
public class ChatStreamClient : IChatStreamClient
{
    private readonly HttpClient _http;

    public ChatStreamClient(HttpClient http) => _http = http;

    public async IAsyncEnumerable<string> StreamAsync(
        SummarizationRequestConfig config, IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Send is split from the read loop so the try/catch around transport errors never wraps a yield.
        var resp = await SendAsync(config, messages, ct);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(resp, ct);
                throw new ChatStreamException($"HTTP {(int)resp.StatusCode}: {Truncate(body, 300)}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                var token = ParseStreamLine(line, out var done);
                if (done) yield break;
                if (!string.IsNullOrEmpty(token)) yield return token!;
            }
        }
    }

    public async IAsyncEnumerable<ChatStreamDelta> StreamChunksAsync(
        SummarizationRequestConfig config, IReadOnlyList<object> messages,
        IReadOnlyList<object>? tools, [EnumeratorCancellation] CancellationToken ct = default)
    {
        object body = tools is { Count: > 0 }
            ? new { model = config.Model, temperature = 0.3, stream = true, messages, tools, tool_choice = "auto" }
            : new { model = config.Model, temperature = 0.3, stream = true, messages };

        var resp = await SendRawAsync(config, body, ct);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var err = await SafeReadAsync(resp, ct);
                throw new ChatStreamException($"HTTP {(int)resp.StatusCode}: {Truncate(err, 300)}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                var delta = ParseStreamChunk(line, out var done);
                if (done) yield break;
                if (delta is not null) yield return delta;
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        SummarizationRequestConfig config, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var body = new
        {
            model = config.Model,
            temperature = 0.3,
            stream = true,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };
        return await SendRawAsync(config, body, ct);
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        SummarizationRequestConfig config, object body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{config.ApiBase.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(body)
        };
        if (!string.IsNullOrEmpty(config.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        try
        {
            return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ChatStreamException(ex.Message);
        }
        finally
        {
            req.Dispose();
        }
    }

    /// <summary>Parses one SSE line. Returns the content delta (or null), and sets
    /// <paramref name="done"/> when the stream's terminal <c>[DONE]</c> marker is seen.</summary>
    public static string? ParseStreamLine(string line, out bool done)
    {
        done = false;
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;

        var data = line["data:".Length..].Trim();
        if (data.Length == 0) return null;
        if (data == "[DONE]") { done = true; return null; }

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array) return null;
            var first = choices.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object
                && first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parses one SSE line into a <see cref="ChatStreamDelta"/> (content delta, tool-call fragments
    /// and/or finish reason). Returns null for non-data/empty lines; sets <paramref name="done"/> on
    /// <c>[DONE]</c>.</summary>
    public static ChatStreamDelta? ParseStreamChunk(string line, out bool done)
    {
        done = false;
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;

        var data = line["data:".Length..].Trim();
        if (data.Length == 0) return null;
        if (data == "[DONE]") { done = true; return null; }

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array) return null;
            var first = choices.EnumerateArray().FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Object) return null;

            string? content = null;
            List<ToolCallFragment>? toolCalls = null;
            string? finish = null;

            if (first.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    content = c.GetString();

                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    toolCalls = [];
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var index = tc.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number
                            ? i.GetInt32() : 0;
                        var id = StringProp(tc, "id");
                        string? name = null, argsFrag = null;
                        if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                        {
                            name = StringProp(fn, "name");
                            argsFrag = StringProp(fn, "arguments");
                        }
                        toolCalls.Add(new ToolCallFragment(index, id, name, argsFrag));
                    }
                }
            }

            if (first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                finish = fr.GetString();

            if (content is null && toolCalls is null && finish is null) return null;
            return new ChatStreamDelta(content, toolCalls, finish);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringProp(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
