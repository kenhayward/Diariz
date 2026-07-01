using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>
/// Pure (no IO) construction of the meeting-minutes chat request and extraction of the Markdown
/// response, so the prompt shape and the response cleaning can be unit-tested without a live endpoint.
/// Unlike the summary prompt, the model returns raw GitHub-flavoured Markdown (not JSON) — minutes need
/// no auto-name field.
/// </summary>
public static class MeetingMinutesPrompt
{
    /// <summary>The locked minutes system prompt: professional, factual, no emojis, GFM Markdown.</summary>
    public const string SystemPrompt =
        "You are a professional meeting-minutes writer. From the transcript provided, produce a complete, " +
        "well-structured set of formal meeting minutes suitable for emailing to the participants. " +
        "Write in clear, professional, neutral English, in the third person and past tense. Base every " +
        "statement strictly on the transcript — do not invent attendees, decisions, dates, figures, or " +
        "commitments it does not support. If a detail (such as the meeting date or a person's full name) is " +
        "not stated, omit it rather than guessing. " +
        "Format the output as GitHub-flavoured Markdown, using headings, bulleted and numbered lists, tables, " +
        "and bold text where they aid clarity. Do not use emojis or decorative symbols. Do not wrap the output " +
        "in code fences, and add no preamble or closing commentary — return only the minutes. " +
        "Structure with these sections, omitting any the transcript does not support: a title (# heading) " +
        "naming the meeting; Overview (one or two sentences); Attendees (the speakers identified in the " +
        "transcript); Discussion (grouped by topic under ## sub-headings with bullet points); Decisions (a " +
        "list); Action Items (a Markdown table with columns Action, Owner, Due date — leave a cell empty if " +
        "the transcript does not specify it); Next Steps (any agreed follow-ups). " +
        "Summarise rather than transcribe; keep it concise and readable.";

    public static IReadOnlyList<ChatMessage> BuildMessages(
        IReadOnlyList<SegmentDto> segments, DateTimeOffset? meetingDate, int charBudget)
    {
        var sb = new StringBuilder();
        if (meetingDate is { } d)
            sb.Append("Meeting date: ").Append(d.ToString("yyyy-MM-dd")).Append("\n\n");
        sb.Append("Transcript:\n").Append(PromptTranscript.Build(segments, charBudget));

        return [new ChatMessage("system", SystemPrompt), new ChatMessage("user", sb.ToString())];
    }

    /// <summary>Extract the model's Markdown from a /chat/completions response, stripping any wrapping code
    /// fence and model control tokens (e.g. gpt-oss "harmony" markers).</summary>
    public static string CleanResponse(string responseJson)
    {
        string content;
        using (var doc = JsonDocument.Parse(responseJson))
        {
            content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim() ?? "";
        }

        content = StripCodeFence(content);
        return StripModelTokens(content).Trim();
    }

    private static string StripModelTokens(string s) =>
        Regex.Replace(s, @"<\|[^|]*\|>", " ");

    private static string StripCodeFence(string s)
    {
        if (!s.StartsWith("```")) return s;
        var firstNl = s.IndexOf('\n');
        if (firstNl < 0) return s;
        var body = s[(firstNl + 1)..];
        var fence = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fence >= 0 ? body[..fence] : body).Trim();
    }
}
