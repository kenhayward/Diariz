using System.Text;

namespace Diariz.Api.Services;

/// <summary>Builds the chat-context text block for a folder (section): its roll-up summary, minutes and
/// aggregated action items, so "chat about this folder" uses the concise roll-ups rather than every full
/// transcript. Pure so it can be unit-tested; the controller supplies the already-loaded pieces.</summary>
public static class ChatFolderContext
{
    public static string BuildText(string? summary, string? minutes, string actionsText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Folder summary:");
        sb.AppendLine(string.IsNullOrWhiteSpace(summary) ? "(no folder summary generated yet)" : summary!.Trim());
        sb.AppendLine();
        sb.AppendLine("Folder minutes:");
        sb.AppendLine(string.IsNullOrWhiteSpace(minutes) ? "(no folder minutes generated yet)" : minutes!.Trim());
        if (!string.IsNullOrWhiteSpace(actionsText))
        {
            sb.AppendLine();
            sb.Append(actionsText.Trim());
        }
        return sb.ToString().Trim();
    }
}
