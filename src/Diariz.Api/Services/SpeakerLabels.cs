using System.Globalization;
using System.Text.RegularExpressions;

namespace Diariz.Api.Services;

/// <summary>Allocates diarization labels for speakers the API mints, so a client never writes into the
/// worker's <c>SPEAKER_NN</c> namespace and cannot collide with a label a later re-transcription
/// produces.</summary>
public static partial class SpeakerLabels
{
    [GeneratedRegex(@"^SPEAKER_(\d+)$")]
    private static partial Regex Numbered();

    /// <summary>The next unused <c>SPEAKER_NN</c>: one past the highest number present, <em>not</em> the
    /// first gap. A gap exists because a speaker was removed, and a re-transcription may well hand that
    /// number back out - reusing it would silently merge two different voices under one label.</summary>
    public static string NextFree(IEnumerable<string> existing)
    {
        var highest = -1;
        foreach (var label in existing)
        {
            var match = Numbered().Match(label ?? "");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var n) && n > highest) highest = n;
        }
        // Two digits minimum, matching pyannote's own format, so hand-minted and diarized labels sort and
        // read alike.
        return $"SPEAKER_{(highest + 1).ToString("D2", CultureInfo.InvariantCulture)}";
    }
}
