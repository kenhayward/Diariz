using System.Text;
using Diariz.Api.Contracts;

namespace Diariz.Api.Services;

/// <summary>Pure (EF-free) rendering of transcript segments into downloadable formats.</summary>
public static class TranscriptFormatter
{
    /// <summary>Readable plain text: one "[mm:ss] Speaker: text" line per segment.</summary>
    public static string ToPlainText(IReadOnlyList<SegmentDto> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in segments)
            sb.Append('[').Append(Clock(s.StartMs)).Append("] ")
              .Append(s.SpeakerDisplay).Append(": ").Append(s.Text).Append('\n');
        return sb.ToString();
    }

    /// <summary>SubRip (.srt) subtitle cues, numbered from 1, blank-line separated.</summary>
    public static string ToSrt(IReadOnlyList<SegmentDto> segments)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            sb.Append(i + 1).Append('\n')
              .Append(SrtTime(s.StartMs)).Append(" --> ").Append(SrtTime(s.EndMs)).Append('\n')
              .Append(s.SpeakerDisplay).Append(": ").Append(s.Text).Append('\n');
            if (i < segments.Count - 1) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
    }

    private static string SrtTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2},{t.Milliseconds:D3}";
    }
}
