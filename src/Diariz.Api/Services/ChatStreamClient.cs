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

public interface IChatStreamClient
{
    /// <summary>Streams the assistant reply token-by-token from an OpenAI-compatible
    /// <c>/chat/completions</c> endpoint (SSE, <c>stream:true</c>), using a per-request config.</summary>
    IAsyncEnumerable<string> StreamAsync(
        SummarizationRequestConfig config, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
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

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
