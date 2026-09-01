namespace Diariz.Api.Services;

/// <summary>
/// Whether the live transcript has fallen too far behind the meeting to be worth continuing, and by how
/// much.
/// <para>
/// The measurement says this should be rare: a 30 s chunk costs about 2.7 s on the production GPU. It
/// exists for the cases the measurement cannot cover - an under-specified deployment, an unexpected
/// burst of concurrent meetings, or a GPU shared with something else.
/// </para>
/// <para>
/// What it does when that happens is the point. Pausing the live transcript costs the live transcript.
/// Capture keeps running, the chunks stay durable, and the final pass transcribes the whole meeting
/// exactly as it would have anyway - so the worst case is that someone loses the running commentary,
/// not the meeting.
/// </para>
/// </summary>
public static class LiveTranscriptLag
{
    /// <summary>Whether to stop queueing live work for a recording.
    /// <para>
    /// <paramref name="oldestOutstanding"/> is when the longest-waiting untranscribed chunk arrived, or
    /// null when nothing is outstanding - which means the transcriber is keeping up, however long the
    /// meeting has run.
    /// </para>
    /// <para>
    /// A non-positive <paramref name="maxLag"/> means "never pause": an operator who would rather the
    /// transcript caught up eventually than stopped. A naive comparison would read zero as "pause
    /// immediately" and silently disable the feature for exactly the person who asked for it to keep
    /// trying.
    /// </para></summary>
    public static bool ShouldPause(DateTimeOffset? oldestOutstanding, DateTimeOffset now, TimeSpan maxLag)
    {
        if (maxLag <= TimeSpan.Zero) return false;
        if (oldestOutstanding is not { } waitingSince) return false;
        return now - waitingSince > maxLag;
    }

    /// <summary>How far behind the transcript is, in seconds, for the status line the user reads.
    /// Clamped at zero: clock skew between the API and its database would otherwise show the transcript
    /// running ahead of the meeting.</summary>
    public static int LagSeconds(DateTimeOffset? oldestOutstanding, DateTimeOffset now)
    {
        if (oldestOutstanding is not { } waitingSince) return 0;
        var seconds = (now - waitingSince).TotalSeconds;
        return seconds <= 0 ? 0 : (int)Math.Round(seconds);
    }
}
