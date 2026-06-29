using System.Text;

namespace Diariz.Api.Services;

/// <summary>One transcript's plain text plus a human-readable title, used as chat context.</summary>
public sealed record TranscriptContext(string Title, string Text);

/// <summary>
/// Pure (no IO) assembly of the chat system prompt from selected transcripts and an optional
/// uploaded attachment, plus the message list (system + history) sent to the model each turn.
/// </summary>
public static class ChatContextBuilder
{
    /// <summary>Upper bound on context characters embedded in the system prompt (keeps requests bounded).</summary>
    public const int DefaultCharBudget = 48_000;

    public static string BuildSystemPrompt(
        IReadOnlyList<TranscriptContext> transcripts, string? attachmentName, string? attachmentText,
        IReadOnlyList<TranscriptContext>? documents = null, int charBudget = DefaultCharBudget)
    {
        var context = new StringBuilder();
        foreach (var t in transcripts)
        {
            context.Append("=== Transcript: ").Append(t.Title).Append(" ===\n");
            context.Append(string.IsNullOrWhiteSpace(t.Text) ? "(no transcript yet)\n" : t.Text);
            context.Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(attachmentText))
            AppendDocument(context, attachmentName, attachmentText);

        // Documents pulled from the selected recordings' attachments (files + fetched URLs).
        foreach (var d in documents ?? [])
            if (!string.IsNullOrWhiteSpace(d.Text))
                AppendDocument(context, d.Title, d.Text);

        var body = context.ToString().Trim();
        if (body.Length > charBudget)
            body = body[..charBudget] + "\n[context truncated]";

        var sb = new StringBuilder();
        sb.Append("You are a helpful assistant answering questions about the meeting transcript(s) below. ");
        sb.Append("Base your answers on the provided context; if the answer is not in the context, say so. ");
        sb.Append("Be concise.\n\n");
        sb.Append(body.Length > 0 ? "Context:\n" + body
            : "No transcript context was provided for this conversation.");
        return sb.ToString();
    }

    private static void AppendDocument(StringBuilder context, string? name, string text)
    {
        context.Append("=== Attached document: ")
            .Append(string.IsNullOrWhiteSpace(name) ? "document" : name).Append(" ===\n");
        context.Append(text).Append('\n');
    }

    /// <summary>The request messages: the system prompt followed by the (non-blank) conversation history.</summary>
    public static IReadOnlyList<ChatMessage> BuildMessages(string systemPrompt, IReadOnlyList<ChatMessage> history)
    {
        var msgs = new List<ChatMessage>(history.Count + 1) { new("system", systemPrompt) };
        foreach (var m in history)
            if (!string.IsNullOrWhiteSpace(m.Content))
                msgs.Add(new ChatMessage(NormalizeRole(m.Role), m.Content));
        return msgs;
    }

    private static string NormalizeRole(string? role) =>
        string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
}
