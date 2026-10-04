using System.Reflection;
using Diariz.Api.Configuration;
using Diariz.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Diariz.Api.Tests;

/// <summary>Trimming the job streams (issue #818).
///
/// <para>Redis streams do not discard an entry when it is acknowledged - the consumer group's pending list
/// empties, the entry stays. Nothing here ever passed a <c>MAXLEN</c>, so all twelve streams grew forever, in
/// memory and in the AOF that replays on every restart. The live-chunk stream grows fastest by orders of
/// magnitude: one entry per audio chunk per recording while a meeting is being transcribed live.</para>
///
/// <para>The danger is the opposite mistake. An entry still <b>pending</b> holds the only copy of its
/// payload: <c>StreamReclaimer</c> reads it back before acking an abandoned message, and its
/// <c>if (entry.Length > 0)</c> means a trimmed entry does not throw - the handler simply never runs, so the
/// recording is never settled and sits in <c>Transcribing</c> with nothing to move it on. A trim that is too
/// aggressive therefore converts a delayed job into a silently lost one, which is worse than unbounded
/// growth. Hence the window is asserted against how long an entry can legitimately stay pending, rather
/// than being a number someone picked.</para></summary>
public class StreamRetentionTests
{
    private static IServiceProvider DefaultOptions() =>
        new ServiceCollection().AddOptions().BuildServiceProvider();

    [Fact]
    public void MinId_IsTheRetentionWindowBeforeNow()
    {
        var now = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        var minId = StreamRetention.MinIdFor(now);

        var cutoffMs = now.Subtract(StreamRetention.Window).ToUnixTimeMilliseconds();
        Assert.Equal($"{cutoffMs}-0", minId);
    }

    /// <summary>The relationship that matters, and the one a future edit could quietly break. An entry can
    /// stay pending for as long as a worker keeps claiming it: <c>StreamReclaimer</c> only takes one over
    /// after <see cref="StreamReclaimer.DefaultMinIdle"/> and gives up after
    /// <see cref="StreamReclaimer.MaxDeliveries"/>, and a live worker refreshing its claim holds it for the
    /// length of the job - the Python worker accepts up to four hours of audio. The window has to clear all
    /// of that with room to spare, so this asserts a large multiple of the abandon window rather than the
    /// bare inequality, which 2 hours would also satisfy while being far too tight.</summary>
    [Fact]
    public void Window_ClearsTheWorstCaseAnEntryCanStayPending()
    {
        var abandonWindow = StreamReclaimer.DefaultMinIdle * StreamReclaimer.MaxDeliveries;

        Assert.True(StreamRetention.Window > abandonWindow * 8,
            $"Retention window is {StreamRetention.Window}, which is not comfortably clear of the " +
            $"{abandonWindow} a message can sit pending before StreamReclaimer abandons it - and a job that " +
            "keeps refreshing its claim holds its entry for hours longer still. Trimming a pending entry " +
            "destroys the only copy of its payload, and the recording is then never settled. See issue #818.");
    }

    /// <summary>Every stream the queue writes to must be trimmed. A new stream added to <c>JobQueue</c> and
    /// not to the trimmer would grow forever exactly as all twelve used to, and no other test would notice -
    /// the partial-guard shape, where a guard covering most of its domain looks like a guard. So the domain
    /// is enumerated from the options rather than restated by hand.</summary>
    [Fact]
    public void EveryStreamKeyInTheOptions_IsTrimmed()
    {
        var trimmed = StreamRetention.StreamKeys(DefaultOptions());

        var declared = typeof(JobQueueOptions).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.Name.EndsWith("Options", StringComparison.Ordinal))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string)
                            && p.Name.EndsWith("StreamKey", StringComparison.Ordinal))
                .Select(p => (string?)p.GetValue(Activator.CreateInstance(t))))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.Empty(declared.Except(trimmed));
    }

    /// <summary>And nothing beyond them: trimming a key the queue does not own would be acting on another
    /// application's data if the Redis instance is ever shared.</summary>
    [Fact]
    public void NothingBeyondTheQueuesOwnStreams_IsTrimmed()
    {
        var trimmed = StreamRetention.StreamKeys(DefaultOptions());

        Assert.Equal(trimmed.Distinct().Count(), trimmed.Count);
        Assert.All(trimmed, k => Assert.EndsWith("-jobs", k, StringComparison.Ordinal));
    }
}
