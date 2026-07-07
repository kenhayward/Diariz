namespace Diariz.Api.Services;

/// <summary>Normalises transcript segment text so it never contains repeated line feeds or blank lines: each
/// non-empty line is trimmed and joined to the next with a single line break, blank lines are dropped, and
/// leading/trailing blank lines are removed. Applied where segment text enters the system (the worker
/// callback) and where segments are merged, so a stored/displayed transcript uses one end-of-paragraph mark
/// between lines, never a blank line. Pure and fully unit-testable.</summary>
public static class TranscriptText
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }
}
