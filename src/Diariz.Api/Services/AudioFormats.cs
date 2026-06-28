namespace Diariz.Api.Services;

/// <summary>
/// Pure, dependency-free recognition of uploaded audio by its leading bytes ("magic"), so we never trust a
/// client-supplied extension/MIME. Decoding is done by ffmpeg in the worker; this just gates what we accept.
/// Canonical formats: "wav", "mp3", "flac", "ogg", "webm", "m4a".
/// </summary>
public static class AudioFormats
{
    // Royalty-free containers + MP3 (its patents expired in 2017) are always allowed. "m4a"/AAC has live
    // patents, so it's gated behind a server flag.
    private static readonly HashSet<string> AlwaysAllowed = new(StringComparer.Ordinal)
    {
        "wav", "mp3", "flac", "ogg", "webm",
    };

    /// <summary>The format detected from the file header, or null if unrecognised / too short.</summary>
    public static string? Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length < 12) return null;

        if (Match(head, 0, "RIFF") && Match(head, 8, "WAVE")) return "wav";
        if (Match(head, 0, "fLaC")) return "flac";
        if (Match(head, 0, "OggS")) return "ogg";
        if (head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3) return "webm"; // EBML (webm/mkv)
        if (Match(head, 4, "ftyp")) return "m4a"; // ISO-BMFF / MP4 family (.m4a, .mp4, .m4b)

        // MP3: an ID3v2 tag, or an MPEG audio frame sync (11 set bits: 0xFF, top 3 bits of next byte).
        if (Match(head, 0, "ID3")) return "mp3";
        if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return "mp3";

        return null;
    }

    /// <summary>Whether a detected format is accepted; "m4a" requires <paramref name="allowAac"/>.</summary>
    public static bool IsAllowed(string format, bool allowAac) =>
        AlwaysAllowed.Contains(format) || (allowAac && format == "m4a");

    /// <summary>Detect + allow-check in one call.</summary>
    public static (bool Ok, string? Format, string? Reason) Validate(ReadOnlySpan<byte> head, bool allowAac)
    {
        var format = Detect(head);
        if (format is null)
            return (false, null, "Unrecognised or unsupported audio file. Try WAV, MP3, FLAC, Ogg/Opus, WebM, or M4A.");
        if (!IsAllowed(format, allowAac))
            return (false, format, format == "m4a"
                ? "M4A/AAC uploads are disabled on this server."
                : $"{format} files aren't accepted.");
        return (true, format, null);
    }

    private static bool Match(ReadOnlySpan<byte> head, int offset, string ascii)
    {
        if (offset + ascii.Length > head.Length) return false;
        for (var i = 0; i < ascii.Length; i++)
            if (head[offset + i] != (byte)ascii[i]) return false;
        return true;
    }
}
