using System.Text;

namespace Diariz.Api.Services;

/// <summary>Sanitises user-influenced values before they go into a log message so a malicious value cannot
/// forge a fake log line (CR/LF injection) or smuggle control characters. Collapses every run of control
/// characters and whitespace into a single space and trims. Pure and allocation-light.</summary>
public static class LogSanitizer
{
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value)
        {
            if (char.IsControl(c) || c == ' ')
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }
}
