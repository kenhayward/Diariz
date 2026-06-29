using System.Text.Json;

namespace Diariz.Api.Services;

public interface IExportLocalizer
{
    /// <summary>Resolve the export labels for a UI-language code (BCP-47), falling back per key to English.
    /// A null/unknown culture yields the English labels.</summary>
    ExportStrings For(string? culture);
}

/// <summary>
/// A tiny JSON string localizer for server-rendered exports/emails: it reads
/// <c>locales/&lt;lang&gt;/exports.json</c> at startup (not compiled <c>.resx</c>), so the same
/// contributor-edits-JSON-only rule applies to server strings as to the web catalogs. Missing keys (or a
/// missing/malformed file) fall back to <see cref="ExportStrings.English"/>.
/// </summary>
public class JsonExportLocalizer : IExportLocalizer
{
    private static readonly Dictionary<string, string> Empty = new();
    private readonly Dictionary<string, Dictionary<string, string>> _byLang = new(StringComparer.OrdinalIgnoreCase);

    public JsonExportLocalizer(string localesRoot)
    {
        if (!Directory.Exists(localesRoot)) return;
        foreach (var dir in Directory.EnumerateDirectories(localesRoot))
        {
            var file = Path.Combine(dir, "exports.json");
            if (!File.Exists(file)) continue;
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (map is not null) _byLang[Path.GetFileName(dir)] = map;
            }
            catch (JsonException)
            {
                // Ignore a malformed catalog; English fallback applies.
            }
        }
    }

    public ExportStrings For(string? culture)
    {
        var map = Resolve(culture);
        var en = ExportStrings.English;
        string V(string key, string fallback) =>
            map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        return new ExportStrings(
            V("transcriptName", en.TranscriptName),
            V("summary", en.Summary),
            V("actions", en.Actions),
            V("transcript", en.Transcript),
            V("action", en.Action),
            V("actor", en.Actor),
            V("deadline", en.Deadline),
            V("time", en.Time),
            V("speaker", en.Speaker),
            V("text", en.Text),
            V("none", en.None),
            V("sentFromDiariz", en.SentFromDiariz),
            V("subject", en.Subject));
    }

    private Dictionary<string, string> Resolve(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return Empty;
        if (_byLang.TryGetValue(culture, out var exact)) return exact;
        var baseTag = culture.Split('-')[0];
        return _byLang.TryGetValue(baseTag, out var bas) ? bas : Empty;
    }
}
