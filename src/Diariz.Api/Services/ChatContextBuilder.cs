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
        IReadOnlyList<TranscriptContext>? documents = null, int charBudget = DefaultCharBudget,
        string? userName = null, string? userEmail = null)
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
        sb.Append("You are Diariz's assistant. You help the user understand and recall their own meeting ");
        sb.Append("recordings and transcripts. Treat the user's questions as being about their recordings ");
        sb.Append("unless they clearly ask for general knowledge: a bare name, company, project, customer, or ");
        sb.Append("topic refers to what was said in their meetings, even if they don't say \"in the transcripts\". ");
        sb.Append("Base your answers on the transcript context below (and any tools available to you). When ");
        sb.Append("something genuinely isn't there, say so briefly. Be concise.\n\n");

        // Identify the current user so the model knows who it is helping — and, when it emails them (the
        // send_email tool always delivers to this address), can write the message as being from them.
        if (!string.IsNullOrWhiteSpace(userName) || !string.IsNullOrWhiteSpace(userEmail))
        {
            sb.Append("You are assisting ");
            sb.Append(string.IsNullOrWhiteSpace(userName) ? "the signed-in user" : userName!.Trim());
            if (!string.IsNullOrWhiteSpace(userEmail)) sb.Append(" (").Append(userEmail!.Trim()).Append(')');
            sb.Append(". Any email you send on their behalf is delivered to this address; write it as being ")
              .Append("from them.\n\n");
        }

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
