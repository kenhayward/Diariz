using Diariz.Api.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Diariz.Api.Services;

/// <summary>Bounds the job streams, which otherwise grow forever (issue #818).
///
/// <para>Redis does not discard a stream entry when it is acknowledged - the consumer group's pending list
/// empties and the entry stays. No <c>MAXLEN</c> was ever passed on the adds, so all twelve streams
/// accumulated every job ever enqueued, held in memory and replayed from the AOF on each restart. The
/// live-chunk stream grows fastest by orders of magnitude: one entry per audio chunk per recording while a
/// meeting is transcribed live.</para>
///
/// <para><b>Why the window is enormous rather than tight.</b> An entry that is still pending holds the only
/// copy of its payload. <see cref="StreamReclaimer"/> reads it back before acking an abandoned message, and
/// its <c>if (entry.Length &gt; 0)</c> guard means a trimmed entry does not throw - the handler simply never
/// runs, so the recording is never settled and sits in <c>Transcribing</c> with nothing to move it on. A trim
/// that is too aggressive therefore turns a delayed job into a silently lost one, which is a worse failure
/// than the growth it fixes. An entry can legitimately stay pending for the length of its job (the Python
/// worker accepts up to four hours of audio and refreshes its claim while working) plus the abandon window
/// (<see cref="StreamReclaimer.DefaultMinIdle"/> times <see cref="StreamReclaimer.MaxDeliveries"/>). Seven
/// days clears all of it by a wide margin while still turning unbounded growth into a fixed ceiling, and
/// <c>StreamRetentionTests</c> asserts that relationship rather than the number.</para>
///
/// <para>Trimming by <b>MINID</b>, not <c>MAXLEN</c>: a count cannot express "older than the longest a job
/// can run", and the safe count differs per stream by orders of magnitude. An ID is a millisecond
/// timestamp, so the cutoff is exact and the same for every stream. And exact rather than approximate - see
/// <see cref="TrimAsync"/>, where the first attempt used <c>~</c> and the test caught it.</para></summary>
public static class StreamRetention
{
    /// <summary>How much history to keep. See the class note for why it is days rather than hours.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>The stream ID to trim below: entries older than the window. Stream IDs are
    /// <c>&lt;unix-ms&gt;-&lt;seq&gt;</c>, and <c>-0</c> is the lowest sequence in that millisecond, so this
    /// is the first ID that must be kept.</summary>
    public static string MinIdFor(DateTimeOffset nowUtc) =>
        $"{nowUtc.Subtract(Window).ToUnixTimeMilliseconds()}-0";

    /// <summary>Every stream <see cref="RedisJobQueue"/> writes to, read from the same options it enqueues
    /// with so the two cannot disagree. A stream added there and missed here would grow forever exactly as
    /// these did - <c>StreamRetentionTests</c> enumerates the options to prove none is missed.</summary>
    public static IReadOnlyList<string> StreamKeys(IServiceProvider services)
    {
        var queue = services.GetRequiredService<IOptions<JobQueueOptions>>().Value;
        return
        [
            queue.StreamKey,
            queue.MergeStreamKey,
            queue.VoiceprintStreamKey,
            queue.LiveChunkStreamKey,
            services.GetRequiredService<IOptions<SummarizationOptions>>().Value.StreamKey,
            services.GetRequiredService<IOptions<MeetingMinutesOptions>>().Value.StreamKey,
            services.GetRequiredService<IOptions<ActionsOptions>>().Value.StreamKey,
            services.GetRequiredService<IOptions<SectionSummaryOptions>>().Value.StreamKey,
            services.GetRequiredService<IOptions<SectionMinutesOptions>>().Value.StreamKey,
            services.GetRequiredService<IOptions<FormulaRunOptions>>().Value.StreamKey,
            services.GetRequiredService<IOptions<TagsOptions>>().Value.StreamKey,
            services.GetRequiredService<IOptions<EmbeddingOptions>>().Value.StreamKey,
        ];
    }

    /// <summary>Trims one pass over every stream. Returns how many entries went, per stream, so the caller
    /// can log one line instead of twelve.</summary>
    public static async Task<long> TrimAsync(
        IDatabase db, IEnumerable<string> streamKeys, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var minId = MinIdFor(nowUtc);
        long removed = 0;

        foreach (var key in streamKeys)
        {
            ct.ThrowIfCancellationRequested();
            // EXACT, not approximate (`~`). Approximate trimming evicts whole radix nodes only, so a stale
            // entry sharing a node with a retained one simply survives - on a short stream that means
            // nothing is trimmed at all, and which entries go depends on how Redis happened to pack them.
            // The integration test caught exactly that: it passed in isolation and failed in the full run.
            // `~` exists to keep trimming O(1) when it rides along with every add at high throughput; this
            // is a once-a-day sweep, and exact XTRIM costs in proportion to the entries it removes, which
            // is bounded by a day's worth. Determinism is worth far more here than the constant factor.
            var result = await db.ExecuteAsync("XTRIM", key, "MINID", minId);
            if (!result.IsNull) removed += (long)result;
        }

        return removed;
    }
}

/// <summary>Runs the trim daily. Singleton host, so it resolves the stream keys from a scope per pass.</summary>
public class StreamRetentionWorker(
    IServiceProvider services,
    IConnectionMultiplexer redis,
    ILogger<StreamRetentionWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var keys = StreamRetention.StreamKeys(scope.ServiceProvider);
                var removed = await StreamRetention.TrimAsync(
                    redis.GetDatabase(), keys, DateTimeOffset.UtcNow, stoppingToken);

                if (removed > 0)
                    logger.LogInformation(
                        "Job stream retention: trimmed {Removed} acknowledged entr(ies) older than {Window} " +
                        "across {Streams} streams.", removed, StreamRetention.Window, keys.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // The streams grow slowly; a missed pass costs nothing and the next is a day away.
                logger.LogWarning(e, "Job stream retention failed; will try again in {Interval}.", Interval);
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
