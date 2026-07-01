namespace Diariz.Api.Services;

/// <summary>Supplies the meeting-minutes prompt template. Reads the editable
/// <c>prompts/meeting-minutes.md</c> so it can be changed (or volume-mounted) without a rebuild; falls back to
/// <see cref="MeetingMinutesPrompt.DefaultTemplate"/> when the file is missing or unreadable.</summary>
public interface IMeetingMinutesPromptProvider
{
    string GetTemplate();
}

public class FileMeetingMinutesPromptProvider : IMeetingMinutesPromptProvider
{
    private readonly string _path;

    public FileMeetingMinutesPromptProvider(string path) => _path = path;

    public string GetTemplate()
    {
        try
        {
            if (File.Exists(_path))
            {
                var text = File.ReadAllText(_path);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch (IOException)
        {
            // File busy/unreadable (e.g. mid-edit) — fall back to the built-in default.
        }
        return MeetingMinutesPrompt.DefaultTemplate;
    }
}
