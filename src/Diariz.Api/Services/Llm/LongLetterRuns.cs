using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Diariz.Api.Services.Llm;

/// <summary>Breaks unbroken runs of letters that a model's tokenizer cannot split (issue #841).
///
/// <para>WHY THIS EXISTS: llama.cpp's tokenizer for gpt-oss, as LM Studio serves it, refuses the whole request
/// with 400 "Failed to process regex" when one run of letters is longer than about 200 characters - measured
/// against the real endpoint at 190 ASCII letters passing and 200 failing, and around 220 for real Japanese.
/// Transcripts reach that two ordinary ways. Japanese and Chinese put no spaces between words, so one long
/// sentence is one run; and Whisper's repetition loops emit 250-letter "words". Either one fails every AI step
/// for the recording, permanently, since the same input fails the same way each time.</para>
///
/// <para>Only LETTERS count (and combining marks, which belong to the letter before them). A digit,
/// punctuation or whitespace already ends a run where the tokenizer splits by itself - 250 letters with a
/// hyphen every 50 pass, as do 300 digits and a 300-character URL - so numbers, links and code are never
/// touched. The fix is a space every <see cref="MaxRun"/> letters, about half the measured limit: a model reads
/// it as nothing more than a line wrap, and for ordinary prose nothing changes at all.</para></summary>
public static class LongLetterRuns
{
    /// <summary>The longest run of letters left intact. Well under the ~200 the endpoint refuses, so a
    /// tokenizer slightly stricter than the one measured is covered too.</summary>
    public const int MaxRun = 100;

    /// <summary>Whether a character continues a letter run: any letter, or a combining mark.</summary>
    public static bool IsRunCharacter(Rune r) => Rune.GetUnicodeCategory(r) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark => true,
        _ => false,
    };

    private static bool IsMark(Rune r) => Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>Returns <paramref name="text"/> with a space inserted wherever a letter run would exceed
    /// <paramref name="maxRun"/>. Returns the <b>same instance</b> when nothing needs breaking, so callers can
    /// tell "unchanged" by reference. Works in runes, so a surrogate pair is never split, and never puts a
    /// space in front of a combining mark, which would detach it from its letter.</summary>
    public static string Break(string text, int maxRun = MaxRun)
    {
        if (!HasRunLongerThan(text, maxRun)) return text;

        var sb = new StringBuilder(text.Length + text.Length / maxRun + 1);
        var run = 0;
        foreach (var r in text.EnumerateRunes())
        {
            if (IsRunCharacter(r))
            {
                if (run >= maxRun && !IsMark(r))
                {
                    sb.Append(' ');
                    run = 0;
                }
                run++;
            }
            else
            {
                run = 0;
            }
            sb.Append(r.ToString());
        }
        return sb.ToString();
    }

    private static bool HasRunLongerThan(string text, int maxRun)
    {
        var run = 0;
        foreach (var r in text.EnumerateRunes())
        {
            run = IsRunCharacter(r) ? run + 1 : 0;
            if (run > maxRun) return true;
        }
        return false;
    }
}

/// <summary>Applies <see cref="LongLetterRuns.Break"/> to the text of every chat message on its way to the
/// model.
///
/// <para>WHY A HANDLER: transcript text reaches the model through more than twenty prompt builders - summaries,
/// minutes, sections, tags, actions, chat, translation, formulas, attachments - and a fix in any one of them
/// would leave the rest failing. Every LLM client goes through <c>AddLlmClient</c>, so this is the one place
/// that sees them all, including clients added later.</para>
///
/// <para>Only the <c>messages</c> of a chat body are rewritten: string <c>content</c>, and the <c>text</c> of
/// array parts. An image part's data URL is one long run of base64 letters and must arrive intact, and an
/// embeddings body (<c>input</c>) is left alone because the embedding model has its own tokenizer and its
/// vectors must match what search later compares them with. A body with nothing to break is sent byte for
/// byte as the client built it.</para></summary>
public sealed class LongLetterRunHandler : DelegatingHandler
{
    private static readonly JsonSerializerOptions Relaxed = new()
    {
        // A request body, never HTML: keep non-ASCII text as itself rather than \u-escaping every character
        // of a Japanese transcript, which would triple its size on the wire.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content?.Headers.ContentType?.MediaType is "application/json")
        {
            var original = await request.Content.ReadAsStringAsync(ct);
            if (Rewrite(original) is { } rewritten)
            {
                var content = new StringContent(rewritten, Encoding.UTF8);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                    request.Content.Headers.ContentType.ToString());
                request.Content = content;
            }
        }
        return await base.SendAsync(request, ct);
    }

    /// <summary>The rewritten body, or null when it is not a chat body or nothing in it needed breaking.</summary>
    public static string? Rewrite(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return null; }

        if (root is not JsonObject obj || obj["messages"] is not JsonArray messages) return null;

        var changed = false;
        foreach (var message in messages.OfType<JsonObject>())
        {
            switch (message["content"])
            {
                case JsonValue v when v.TryGetValue<string>(out var text):
                    changed |= Replace(message, "content", text);
                    break;
                case JsonArray parts:
                    foreach (var part in parts.OfType<JsonObject>())
                        if (part["type"]?.GetValue<string>() == "text" &&
                            part["text"] is JsonValue t && t.TryGetValue<string>(out var partText))
                            changed |= Replace(part, "text", partText);
                    break;
            }
        }
        return changed ? root.ToJsonString(Relaxed) : null;
    }

    private static bool Replace(JsonObject owner, string key, string text)
    {
        var broken = LongLetterRuns.Break(text);
        if (ReferenceEquals(broken, text)) return false;
        owner[key] = broken;
        return true;
    }
}
