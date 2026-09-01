using Diariz.Api.Services;

namespace Diariz.Api.Tests;

/// <summary>
/// When the transcriber cannot keep up. The whole point is that this is a degradation of the live
/// transcript and nothing else: capture keeps running, the audio stays durable, and the final pass
/// covers the whole meeting regardless.
/// </summary>
public class LiveTranscriptLagTests
{
    private static readonly TimeSpan MaxLag = TimeSpan.FromSeconds(120);
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(121, true)]
    [InlineData(119, false)]
    // Exactly at the threshold is NOT yet degraded. Without this case the comparison could be > or >=
    // and no test would notice - which is precisely what happened to the reaper threshold in phase 1,
    // where the plan's own suggested mutation did not discriminate.
    [InlineData(120, false)]
    [InlineData(0, false)]
    public void ShouldPause_OnlyOnceTheOldestChunkHasWaitedLongerThanTheLimit(int secondsWaiting, bool expected) =>
        Assert.Equal(expected, LiveTranscriptLag.ShouldPause(
            Now.AddSeconds(-secondsWaiting), Now, MaxLag));

    [Fact]
    public void ShouldPause_WithNothingOutstanding_IsFalse()
    {
        // Nothing waiting means the transcriber is keeping up, however long the meeting has run.
        Assert.False(LiveTranscriptLag.ShouldPause(null, Now, MaxLag));
    }

    [Fact]
    public void ShouldPause_WithANonPositiveLimit_NeverPauses()
    {
        // 0 means "never pause on lag" - an operator who wants the transcript to catch up eventually
        // rather than stop. A limit of zero must not mean "pause immediately", which is what a naive
        // comparison would do and would disable the feature for anyone who set it that way.
        Assert.False(LiveTranscriptLag.ShouldPause(Now.AddHours(-1), Now, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(45, 45)]
    public void LagSeconds_ReportsHowFarBehindTheTranscriptIs(int waited, int expected) =>
        Assert.Equal(expected, LiveTranscriptLag.LagSeconds(Now.AddSeconds(-waited), Now));

    [Fact]
    public void LagSeconds_WithNothingOutstanding_IsZero() =>
        Assert.Equal(0, LiveTranscriptLag.LagSeconds(null, Now));

    [Fact]
    public void LagSeconds_NeverGoesNegative()
    {
        // Clock skew between the API and its database would otherwise produce a negative lag, which the
        // status line would render as the transcript being ahead of the meeting.
        Assert.Equal(0, LiveTranscriptLag.LagSeconds(Now.AddSeconds(30), Now));
    }
}
