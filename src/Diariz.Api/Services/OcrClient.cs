using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Diariz.Api.Services.Llm;

namespace Diariz.Api.Services;

public interface IOcrClient
{
    /// <summary>Reads the text off one image, given as a <c>data:</c> URL. Returns whatever the model
    /// produced, verbatim and untrimmed of its own formatting - the caller decides how to present it.</summary>
    Task<string> ExtractAsync(LlmRequestConfig config, string dataUrl, CancellationToken ct = default);
}

/// <summary>Calls an OpenAI-compatible <c>/chat/completions</c> endpoint to read text off one image.
///
/// <para>Modelled on <see cref="TranslationClient"/> - same body dictionary, same
/// <see cref="LlmRequestBody.Apply"/>, same linked-CTS timeout - and it differs from the chat path in
/// exactly three measured ways.</para>
///
/// <list type="number">
///   <item><b>One turn, no system message, no history.</b> The models this serves say plainly on their own
///   cards that they do not do multi-turn conversation. Sending chat's transcript system prompt to one of
///   them would be sending a page of irrelevant text to a 0.9B model with a 12k window.</item>
///   <item><b>The image part comes FIRST.</b> That is llama.cpp's documented ordering for OCR models.
///   <c>ChatToolOrchestrator.Shape</c> puts the text first, correctly for chat, which is precisely why this
///   client builds its own body rather than borrowing that one.</item>
///   <item><b>Never streams and never offers tools</b>, whatever the resolved parameters say. There is no
///   partial answer worth rendering for a single OCR pass, and no OCR model does tool calling.</item>
/// </list>
///
/// <para>The prompt is not written here. It comes from the resolved <c>ocr_prompt</c> parameter and goes
/// on the wire verbatim, because which words to use is a per-model fact an administrator measures - the
/// same capture, the same model, a different prompt, produced a fifth as much text.</para></summary>
public class OcrClient(HttpClient http) : IOcrClient
{
    public async Task<string> ExtractAsync(
        LlmRequestConfig config, string dataUrl, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
            ["messages"] = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = dataUrl } },
                        new { type = "text", text = config.Parameters.OcrPrompt },
                    },
                },
            },
        };
        LlmRequestBody.Apply(body, config.Parameters);

        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{config.ApiBase.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        // A local LM Studio endpoint needs no key, and an empty Bearer is worse than none - some servers
        // reject it outright.
        if (!string.IsNullOrEmpty(config.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        using var resp = await http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        // A model that answers with no choices is a failed extraction, not a crash: the caller already
        // treats empty text as an unprocessable result and says so.
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return "";

        return choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
                ? content.GetString() ?? ""
                : "";
    }
}
